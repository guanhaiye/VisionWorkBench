using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public sealed class TcpOfflineResultEventArgs(
    long taskId, string requestId, DateTimeOffset receivedAt, RecordCompletedEventArgs result) : EventArgs
{
    public long TaskId { get; } = taskId;
    public string RequestId { get; } = requestId;
    public DateTimeOffset ReceivedAt { get; } = receivedAt;
    public RecordCompletedEventArgs Result { get; } = result;
}

/// <summary>
/// TCP 触发执行器。物理相机按 Provider+Device 复用一次，算法会话按任务和模型配置建立有界独立会话池。
/// 每个 TCP 请求只消费一个独立帧，并拥有自己的 DetectionRunService/结果上下文。
/// </summary>
public sealed class TcpTaskExecutionService(AppServices services) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SharedCameraRuntime> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, InferenceLatencyWindow> _inferenceLatency = new();
    private readonly ConcurrentDictionary<string, int> _activeProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _projectIdle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _stoppingProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private int _activeExecutions;
    private int _disposed;

    /// <summary>离线 TCP 批次完成单张推理后通知实时页显示对应原图和检测框。</summary>
    public event EventHandler<TcpOfflineResultEventArgs>? OfflineResultCompleted;

    private sealed record ModelSessionLeaseSet(
        IAlgorithmSession Primary,
        IReadOnlyDictionary<string, IAlgorithmSession> Sop,
        IReadOnlyList<AlgorithmSessionCache.SessionUseLease> Leases);

    public async Task<TcpTaskExecutionResult> ExecuteAsync(
        TcpTaskExecutionRequest request,
        CancellationToken cancellationToken)
    {
        await EnterProjectAsync(request.ProjectCode, cancellationToken);
        try { return await ExecuteCoreAsync(request, cancellationToken); }
        finally { ExitProject(request.ProjectCode); }
    }

    private async Task<TcpTaskExecutionResult> ExecuteCoreAsync(
        TcpTaskExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var task = await services.Tasks.FindAsync(request.TaskId, cancellationToken)
            ?? throw new InvalidOperationException($"任务不存在: {request.TaskId}");
        var found = await services.Recipes.FindAsync(task.Id, cancellationToken)
            ?? throw new InvalidOperationException($"任务配置不存在: {task.Name}");
        var recipe = found.Recipe;
        if (string.Equals(recipe.CameraProviderId, "image-folder", StringComparison.OrdinalIgnoreCase))
            return await ExecuteOfflineImageFolderAsync(request, task, recipe, cancellationToken);

        var camera = _cameras.GetOrAdd(CameraKey(recipe), _ => new SharedCameraRuntime(services, recipe));
        await camera.EnsureStartedAsync(cancellationToken);
        var modelSessions = await AcquireModelSessionsAsync(
            task.Id, recipe, request.MaxConcurrentModelSessions, cancellationToken);
        try
        {
            await using var assignedCamera = camera.CreateAssignedSession(request.ReceivedAt);
            var run = new DetectionRunService(
                services.Records,
                services.TempImages,
                services.LoggerFactory.CreateLogger<DetectionRunService>(),
                $"tcp-{task.Id}-{request.RequestId}",
                services.ResultPublisher,
                () => services.Settings.EnableHistory,
                () => services.Settings.EnableHistory,
                sopRuns: services.SopRuns,
                pendingReplayTrigger: services.SopProductResultReplayer);
            var completion = new TaskCompletionSource<RecordCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            run.RecordCompleted += (_, result) => completion.TrySetResult(result);
            run.Faulted += (_, fault) => completion.TrySetException(new InvalidOperationException($"{fault.Code}: {fault.Message}"));

            var batchService = new BatchService(services.Batches);
            BatchEntity? batch = null;
            var batchEnded = false;
            try
            {
                batch = await batchService.StartAsync(
                    task.Id, run.Counting.State.CurrentTotal, request.ProjectCode, task.StationCode, cancellationToken);
                await run.StartAsync(recipe, task.Id, assignedCamera, modelSessions.Primary, batch.Id,
                    FrameRoutingStrategy.Bounded, cancellationToken, projectId: request.ProjectCode,
                    sopAlgorithms: recipe.Sop?.Definition is not null ? modelSessions.Sop : null);
                var result = await completion.Task.WaitAsync(cancellationToken);
                await run.StopAsync();
                await batchService.EndAsync(result.CountAfter, "completed", cancellationToken);
                batchEnded = true;
                return new TcpTaskExecutionResult("completed", result.CountAfter, result.Decision.Status.ToString(), result.Record.Id);
            }
            finally
            {
                await run.DisposeAsync();
                if (batch is not null && !batchEnded)
                    await batchService.EndAsync(run.Counting.State.CurrentTotal, "aborted", CancellationToken.None);
            }
        }
        finally
        {
            await ReleaseModelSessionsAsync(modelSessions);
        }
    }

    private async Task<TcpTaskExecutionResult> ExecuteOfflineImageFolderAsync(
        TcpTaskExecutionRequest request, TaskEntity task, Recipe recipe, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(recipe.CameraDeviceId))
            throw new DirectoryNotFoundException($"图片目录不存在: {recipe.CameraDeviceId}");
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
        var files = request.ImageFiles?.ToArray() ?? Directory.EnumerateFiles(recipe.CameraDeviceId, "*.*", SearchOption.TopDirectoryOnly)
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException("图片目录没有可检测的图片");

        var descriptor = new CameraDescriptor
        {
            ProviderId = recipe.CameraProviderId,
            DeviceId = recipe.CameraDeviceId,
            DisplayName = recipe.CameraDeviceId,
        };
        var cameraOptions = services.ApplyCameraDefaults(descriptor, new CameraOpenOptions
        {
            Loop = false,
            FrameIntervalMs = 0,
            ImageFiles = files,
            Parameters = recipe.CameraParameters,
        });
        await using var camera = await services.Cameras.OpenSessionAsync(descriptor, cameraOptions, cancellationToken);
        await camera.OpenAsync(cameraOptions, cancellationToken);
        var modelSessions = await AcquireModelSessionsAsync(
            task.Id, recipe, request.MaxConcurrentModelSessions, cancellationToken);
        try
        {
            var run = new DetectionRunService(
                services.Records,
                services.TempImages,
                services.LoggerFactory.CreateLogger<DetectionRunService>(),
                $"tcp-{task.Id}-{request.RequestId}",
                services.ResultPublisher,
                () => services.Settings.EnableHistory,
                () => services.Settings.EnableHistory,
                sopRuns: services.SopRuns,
                pendingReplayTrigger: services.SopProductResultReplayer);
            var sourceCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            RecordCompletedEventArgs? lastRecord = null;
            var progressTracker = new TcpOfflineExecutionProgressTracker(request.Progress);
            run.RecordCompleted += (_, result) =>
            {
                lastRecord = result;
                progressTracker.ReportProcessed();
                RecordInferenceDuration(task.Id, result.Output.Performance?.TotalMs ?? 0);
                var handlers = OfflineResultCompleted;
                if (handlers is null) return;
                var args = new TcpOfflineResultEventArgs(task.Id, request.RequestId, request.ReceivedAt, result);
                foreach (EventHandler<TcpOfflineResultEventArgs> handler in handlers.GetInvocationList())
                {
                    try { handler(this, args); }
                    catch (Exception ex)
                    {
                        services.LoggerFactory.CreateLogger<TcpTaskExecutionService>()
                            .LogDebug(ex, "转发 TCP 离线检测结果到实时画布失败: {RequestId}", request.RequestId);
                    }
                }
            };
            run.SourceCompleted += (_, _) => sourceCompleted.TrySetResult(true);
            run.Faulted += (_, fault) => faulted.TrySetResult(new InvalidOperationException($"{fault.Code}: {fault.Message}"));

            var batchService = new BatchService(services.Batches);
            BatchEntity? batch = null;
            var batchEnded = false;
            try
            {
                batch = await batchService.StartAsync(
                    task.Id, run.Counting.State.CurrentTotal, request.ProjectCode, task.StationCode, cancellationToken);
                await run.StartAsync(recipe, task.Id, camera, modelSessions.Primary, batch.Id,
                    FrameRoutingStrategy.Bounded, cancellationToken, projectId: request.ProjectCode,
                    sopAlgorithms: recipe.Sop?.Definition is not null ? modelSessions.Sop : null);
                var signal = await Task.WhenAny(sourceCompleted.Task, faulted.Task).WaitAsync(cancellationToken);
                if (signal == faulted.Task) throw await faulted.Task;
                await sourceCompleted.Task.WaitAsync(cancellationToken);
                await run.WaitForCompletionAsync(cancellationToken: cancellationToken);
                if (faulted.Task.IsCompleted) throw await faulted.Task;

                var skipped = (camera as IImageFolderCameraSession)?.SkippedFileCount ?? 0;
                var result = CreateOfflineResult(request, files.Length, progressTracker, skipped, lastRecord);
                await run.StopAsync();
                await batchService.EndAsync(run.Counting.State.CurrentTotal, result.Status, CancellationToken.None);
                batchEnded = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                request.Progress?.SetSkippedCount((camera as IImageFolderCameraSession)?.SkippedFileCount ?? 0);
                await run.StopAsync();
                if (batch is not null && !batchEnded)
                {
                    await batchService.EndAsync(run.Counting.State.CurrentTotal, "aborted", CancellationToken.None);
                    batchEnded = true;
                }
                throw;
            }
            catch (Exception ex)
            {
                var skipped = (camera as IImageFolderCameraSession)?.SkippedFileCount ?? 0;
                var partial = CreateOfflineResult(request, files.Length, progressTracker, skipped, lastRecord);
                if (partial.ProcessedCount == 0 && partial.SkippedCount == 0)
                    throw;

                partial = partial with
                {
                    Status = "partial_failure",
                    Decision = $"{partial.Decision}; {ex.Message}",
                };
                await run.StopAsync();
                if (batch is not null && !batchEnded)
                {
                    await batchService.EndAsync(run.Counting.State.CurrentTotal, partial.Status, CancellationToken.None);
                    batchEnded = true;
                }
                return partial;
            }
            finally
            {
                await run.DisposeAsync();
                if (batch is not null && !batchEnded)
                    await batchService.EndAsync(run.Counting.State.CurrentTotal, "aborted", CancellationToken.None);
            }
        }
        finally
        {
            await ReleaseModelSessionsAsync(modelSessions);
        }
    }
    /// <summary>后台预热所有启用 TCP 触发的任务：提前完成算法 Worker 握手与模型加载，
    /// 使首次 TCP 触发直接复用热会话，避免 Python/TensorRT 冷启动挤占 30 秒执行超时。单个任务失败只记录日志。</summary>
    public void RecordInferenceDuration(long taskId, double elapsedMilliseconds)
    {
        if (taskId <= 0 || !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds <= 0) return;
        _inferenceLatency.GetOrAdd(taskId, _ => new InferenceLatencyWindow()).Add(TimeSpan.FromMilliseconds(elapsedMilliseconds));
    }

    public async Task<TcpExecutionPlan> CreateExecutionPlanAsync(
        long taskId, TimeSpan configuredTimeout, CancellationToken cancellationToken = default)
    {
        var found = await services.Recipes.FindAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException($"任务配置不存在: {taskId}");
        var recipe = found.Recipe;
        if (!string.Equals(recipe.CameraProviderId, "image-folder", StringComparison.OrdinalIgnoreCase))
            return new TcpExecutionPlan(configuredTimeout);

        if (!Directory.Exists(recipe.CameraDeviceId))
            throw new DirectoryNotFoundException($"图片目录不存在: {recipe.CameraDeviceId}");
        var files = Directory.EnumerateFiles(recipe.CameraDeviceId, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
        var measured = _inferenceLatency.TryGetValue(taskId, out var window) ? window.GetP90() : null;
        var timeout = TcpOfflineExecutionPolicy.CalculateTimeout(files.Length, configuredTimeout, measured);
        return new TcpExecutionPlan(timeout, files);
    }

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    private sealed class InferenceLatencyWindow
    {
        private readonly object _gate = new();
        private readonly Queue<TimeSpan> _samples = new();

        public void Add(TimeSpan sample)
        {
            lock (_gate)
            {
                _samples.Enqueue(sample);
                while (_samples.Count > 32) _samples.Dequeue();
            }
        }

        public TimeSpan? GetP90()
        {
            lock (_gate)
            {
                if (_samples.Count == 0) return null;
                var ordered = _samples.OrderBy(value => value).ToArray();
                return ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * 0.9) - 1, 0, ordered.Length - 1)];
            }
        }
    }
    public async Task PrewarmConfiguredTasksAsync(CancellationToken cancellationToken = default)
    {
        var logger = services.LoggerFactory.CreateLogger<TcpTaskExecutionService>();
        try
        {
            var tasks = await services.Tasks.ListAsync(cancellationToken);
            var profiles = new TcpCommunicationProfileStore(services.Settings.ConfigDirectory).Load();
            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTcpTriggerEnabled(task.TriggerJson)) continue;
                try
                {
                    var concurrency = TcpModelSessionPrewarmPolicy.GetConcurrency(task.TriggerJson, profiles);
                    await PrewarmTaskAsync(task.Id, concurrency, cancellationToken);
                    logger.LogInformation("TCP 任务模型池预热完成: {Task} ({Station}), 会话数={Concurrency}",
                        task.Name, task.StationCode, concurrency);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "TCP 任务模型预热失败: {Task} ({Station})", task.Name, task.StationCode);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TCP 任务预热扫描失败");
        }
    }

    /// <summary>预热单个任务的算法模型会话（幂等，重复调用不会重复创建）。相机在请求到达时再打开，避免与界面抢占物理相机。</summary>
    public async Task PrewarmTaskAsync(long taskId, CancellationToken cancellationToken = default)
        => await PrewarmTaskAsync(taskId, 1, cancellationToken);

    private async Task PrewarmTaskAsync(
        long taskId, int maxConcurrentSessions, CancellationToken cancellationToken)
    {
        var task = await services.Tasks.FindAsync(taskId, cancellationToken);
        if (task is null) return;
        var found = await services.Recipes.FindAsync(task.Id, cancellationToken);
        if (found?.Recipe is not { } recipe) return;

        await PrewarmModelSessionsAsync(task.Id, recipe, maxConcurrentSessions, cancellationToken);
    }

    private static bool IsTcpTriggerEnabled(string? triggerJson)
    {
        if (string.IsNullOrWhiteSpace(triggerJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(triggerJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var rule in document.RootElement.EnumerateArray())
            {
                if (rule.TryGetProperty("Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                    return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static TcpTaskExecutionResult CreateOfflineResult(
        TcpTaskExecutionRequest request,
        int totalCount,
        TcpOfflineExecutionProgressTracker tracker,
        int skipped,
        RecordCompletedEventArgs? lastRecord)
    {
        var progress = request.Progress ?? new TcpExecutionProgress(totalCount);
        progress.SetSkippedCount(skipped);
        if (request.Progress is null)
            progress.ReportProcessed(tracker.ProcessedCount);
        return TcpOfflineExecutionPolicy.CreateResult(
            progress.Snapshot(),
            lastRecord?.CountAfter ?? 0,
            lastRecord?.Decision.Status.ToString() ?? (skipped == totalCount ? "all_images_skipped" : "incomplete"),
            lastRecord?.Record.Id ?? 0);
    }

    private async Task<ModelSessionLeaseSet> AcquireModelSessionsAsync(
        long taskId,
        Recipe recipe,
        int maxConcurrentSessions,
        CancellationToken cancellationToken)
    {
        var definitions = recipe.Sop?.Definition?.Steps
            .OrderBy(step => step.Order)
            .Where(step => !string.IsNullOrWhiteSpace(step.Execution?.PluginId))
            .Select(step => (step.Id, Execution: step.Execution!))
            .ToArray() ?? [];
        if (definitions.Length == 0)
        {
            definitions = [("__root", new SopStepExecution
            {
                PluginId = recipe.PluginId,
                TaskType = recipe.TaskType,
                ExecutionProvider = recipe.ExecutionProvider,
                SettingsJson = recipe.SettingsJson,
                Roi = recipe.Roi,
                RoiPolicy = recipe.RoiPolicy,
                Rules = recipe.Rules,
            })];
        }

        var sessions = new Dictionary<string, IAlgorithmSession>(StringComparer.Ordinal);
        var leasesByModel = new Dictionary<string, AlgorithmSessionCache.SessionUseLease>(StringComparer.Ordinal);
        try
        {
            foreach (var model in definitions)
            {
                var modelRecipe = recipe with
                {
                    PluginId = model.Execution.PluginId,
                    TaskType = model.Execution.TaskType,
                    ExecutionProvider = model.Execution.ExecutionProvider,
                    SettingsJson = model.Execution.SettingsJson,
                    Roi = model.Execution.Roi,
                    RoiPolicy = model.Execution.RoiPolicy,
                    Rules = model.Execution.Rules,
                };
                var initialization = new AlgorithmInitialization
                {
                    Settings = DetectionRunService.BuildAlgorithmSettings(modelRecipe),
                    ExecutionProvider = model.Execution.ExecutionProvider,
                };
                var modelKey = string.Join("\\n",
                    model.Execution.PluginId.Trim(),
                    model.Execution.ExecutionProvider.Trim().ToLowerInvariant(),
                    initialization.Settings?.GetRawText() ?? "null");
                if (!leasesByModel.TryGetValue(modelKey, out var lease))
                {
                    lease = await services.AlgorithmSessions.AcquireSessionUseAsync(
                        taskId, model.Execution.PluginId, initialization, maxConcurrentSessions, cancellationToken);
                    leasesByModel.Add(modelKey, lease);
                }
                sessions[model.Id] = lease.Session;
            }
            return new ModelSessionLeaseSet(sessions.Values.First(), sessions, leasesByModel.Values.ToArray());
        }
        catch
        {
            foreach (var lease in leasesByModel.Values.Reverse())
                await lease.DisposeAsync();
            throw;
        }
    }

    private static async Task ReleaseModelSessionsAsync(ModelSessionLeaseSet modelSessions)
    {
        for (var index = modelSessions.Leases.Count - 1; index >= 0; index--)
            await modelSessions.Leases[index].DisposeAsync();
    }

    private async Task PrewarmModelSessionsAsync(
        long recipeTaskId, Recipe recipe, int maxConcurrentSessions, CancellationToken cancellationToken)
    {
        var definitions = recipe.Sop?.Definition?.Steps
            .OrderBy(step => step.Order)
            .Where(step => !string.IsNullOrWhiteSpace(step.Execution?.PluginId))
            .Select(step => (step.Id, Execution: step.Execution!))
            .ToArray() ?? [];
        if (definitions.Length == 0)
        {
            definitions = [("__root", new SopStepExecution
            {
                PluginId = recipe.PluginId,
                TaskType = recipe.TaskType,
                ExecutionProvider = recipe.ExecutionProvider,
                SettingsJson = recipe.SettingsJson,
                Roi = recipe.Roi,
                RoiPolicy = recipe.RoiPolicy,
                Rules = recipe.Rules,
            })];
        }

        var capacity = Math.Clamp(maxConcurrentSessions, 1, AlgorithmSessionCache.MaxSessionPoolSize);
        foreach (var model in definitions)
        {
            var modelRecipe = recipe with
            {
                PluginId = model.Execution.PluginId,
                TaskType = model.Execution.TaskType,
                ExecutionProvider = model.Execution.ExecutionProvider,
                SettingsJson = model.Execution.SettingsJson,
                Roi = model.Execution.Roi,
                RoiPolicy = model.Execution.RoiPolicy,
                Rules = model.Execution.Rules,
            };
            var initialization = new AlgorithmInitialization
            {
                Settings = DetectionRunService.BuildAlgorithmSettings(modelRecipe),
                ExecutionProvider = model.Execution.ExecutionProvider,
            };
            await services.AlgorithmSessions.PrewarmPoolAsync(
                recipeTaskId, model.Execution.PluginId, initialization, capacity, cancellationToken);

        }
    }
    private static string CameraKey(Recipe recipe) => $"{recipe.CameraProviderId.Trim()}::{recipe.CameraDeviceId.Trim()}";

    private async Task EnterProjectAsync(string projectCode, CancellationToken cancellationToken)
    {
        await _resourceGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _stoppingProjects.TryRemove(projectCode, out _);
            _projectIdle.TryRemove(projectCode, out _);
            _activeProjects.AddOrUpdate(projectCode, 1, (_, count) => count + 1);
            Interlocked.Increment(ref _activeExecutions);
        }
        finally { _resourceGate.Release(); }
    }

    private void ExitProject(string projectCode)
    {
        _activeProjects.AddOrUpdate(projectCode, 0, (_, count) => Math.Max(0, count - 1));
        Interlocked.Decrement(ref _activeExecutions);
        if (_activeProjects.TryGetValue(projectCode, out var count) && count == 0
            && _projectIdle.TryGetValue(projectCode, out var idle))
            idle.TrySetResult(true);
        if (Volatile.Read(ref _activeExecutions) == 0 && !_stoppingProjects.IsEmpty)
            _ = TrimIdleResourcesAsync();
    }

    /// <summary>TCP 项目停止时取消后的执行实例全部退出后回收空闲相机和模型会话。</summary>
    public async Task ReleaseProjectAsync(string projectCode)
    {
        _stoppingProjects[projectCode] = 0;
        if (_activeProjects.TryGetValue(projectCode, out var active) && active > 0)
        {
            var idle = _projectIdle.GetOrAdd(projectCode,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            try { await idle.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        await TrimIdleResourcesAsync();
    }

    private async Task TrimIdleResourcesAsync()
    {
        await _resourceGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _activeExecutions) != 0 || _stoppingProjects.IsEmpty) return;
            foreach (var camera in _cameras.Values)
                try { await camera.DisposeAsync(); } catch { }
            _cameras.Clear();
            _stoppingProjects.Clear();
        }
        finally { _resourceGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _resourceGate.WaitAsync();
        try
        {
            foreach (var camera in _cameras.Values) await camera.DisposeAsync();
            _cameras.Clear();
        }
        finally { _resourceGate.Release(); _resourceGate.Dispose(); }
    }

    private sealed class SharedCameraRuntime : IAsyncDisposable
    {
        private readonly AppServices _services;
        private readonly Recipe _recipe;
        private readonly TriggerFrameDistributor _frames = new();
        private readonly SemaphoreSlim _startGate = new(1, 1);
        private readonly CancellationTokenSource _runtimeStop = new();
        private ICameraSession? _camera;
        private Task? _startTask;

        public SharedCameraRuntime(AppServices services, Recipe recipe)
        {
            _services = services;
            _recipe = recipe;
        }

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            await _startGate.WaitAsync(cancellationToken);
            try { _startTask ??= StartCoreAsync(_runtimeStop.Token); }
            finally { _startGate.Release(); }
            await _startTask.WaitAsync(cancellationToken);
        }

        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            var descriptor = new CameraDescriptor
            {
                ProviderId = _recipe.CameraProviderId,
                DeviceId = _recipe.CameraDeviceId,
                DisplayName = _recipe.CameraDeviceId,
            };
            var options = _services.ApplyCameraDefaults(descriptor, new CameraOpenOptions
            {
                FrameIntervalMs = 50,
                Loop = true,
                MaxFrames = null,
                Parameters = _recipe.CameraParameters,
            });
            _camera = await _services.Cameras.OpenSessionAsync(descriptor, options, cancellationToken);
            _camera.FrameReceived += OnFrame;
            await _camera.OpenAsync(options, cancellationToken);
            await _camera.StartAsync(cancellationToken);
        }

        private void OnFrame(object? sender, VideoFrameReceivedEventArgs args) => _frames.Publish(args.Frame);

        public AssignedFrameCameraSession CreateAssignedSession(DateTimeOffset receivedAt) => new(this, receivedAt);

        public async ValueTask DisposeAsync()
        {
            _runtimeStop.Cancel();
            _frames.Complete(new OperationCanceledException("共享相机已停止"));
            if (_startTask is not null)
            {
                try { await _startTask; } catch { }
            }
            if (_camera is not null)
            {
                _camera.FrameReceived -= OnFrame;
                try { await _camera.StopAsync(CancellationToken.None); } catch { }
                await _camera.DisposeAsync();
            }
            _startGate.Dispose();
            _runtimeStop.Dispose();
        }

        public sealed class AssignedFrameCameraSession(SharedCameraRuntime owner, DateTimeOffset receivedAt) : ICameraSession
        {
            private CameraSessionState _state = CameraSessionState.Idle;
            public CameraDescriptor Descriptor => owner._camera?.Descriptor ?? new CameraDescriptor
            {
                ProviderId = owner._recipe.CameraProviderId,
                DeviceId = owner._recipe.CameraDeviceId,
                DisplayName = owner._recipe.CameraDeviceId,
            };
            public CameraSessionState State => _state;
            public CameraCapabilities Capabilities => owner._camera?.Capabilities ?? new();
            public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
            event EventHandler<CameraFaultedEventArgs>? ICameraSession.Faulted { add { } remove { } }
            event EventHandler? ICameraSession.Completed { add { } remove { } }

            public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken)
            {
                _state = CameraSessionState.Opening;
                _state = CameraSessionState.Idle;
                return Task.CompletedTask;
            }

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                _state = CameraSessionState.Streaming;
                var frame = await owner._frames.WaitForFrameAsync(receivedAt, cancellationToken);
                FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(frame));
            }

            public Task PauseAsync(CancellationToken cancellationToken) { _state = CameraSessionState.Paused; return Task.CompletedTask; }
            public Task ResumeAsync(CancellationToken cancellationToken) { _state = CameraSessionState.Streaming; return Task.CompletedTask; }
            public Task StopAsync(CancellationToken cancellationToken) { _state = CameraSessionState.Closed; return Task.CompletedTask; }
            public Task ApplyParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _state = CameraSessionState.Closed; return ValueTask.CompletedTask; }
        }
    }


}
