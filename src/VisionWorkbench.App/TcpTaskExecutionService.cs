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

/// <summary>
/// TCP 触发执行器。物理相机按 Provider+Device 复用一次，算法会话按任务建立最多三个独立会话。
/// 每个 TCP 请求只消费一个独立帧，并拥有自己的 DetectionRunService/结果上下文。
/// </summary>
public sealed class TcpTaskExecutionService(AppServices services) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SharedCameraRuntime> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _projectIdle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _stoppingProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private int _activeExecutions;
    private int _disposed;

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
        var modelSessions = await GetModelSessionsAsync(recipe, cancellationToken);
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

        var modelUses = new List<AlgorithmSessionCache.SessionUseLease>();
        try
        {
            foreach (var session in modelSessions.Sop.Values
                .DistinctBy(session => session.SessionId)
                .OrderBy(session => session.SessionId, StringComparer.Ordinal))
                modelUses.Add(await services.AlgorithmSessions.AcquireUseAsync(session, cancellationToken));

            var batch = await new BatchService(services.Batches).ResumeOrStartAsync(
                task.Id, run.Counting, services.Records, services.Settings.DataDirectory, task.StationCode, cancellationToken);
            await run.StartAsync(recipe, task.Id, assignedCamera, modelSessions.Primary, batch.Id,
                FrameRoutingStrategy.Bounded, cancellationToken, projectId: request.ProjectCode,
                sopAlgorithms: recipe.Sop?.Definition is not null ? modelSessions.Sop : null);
            var result = await completion.Task.WaitAsync(cancellationToken);
            await run.StopAsync();
            await new BatchService(services.Batches).EndAsync(result.CountAfter, "completed", cancellationToken);
            return new TcpTaskExecutionResult("completed", result.CountAfter, result.Decision.Status.ToString(), result.Record.Id);
        }
        finally
        {
            await run.DisposeAsync();
            for (var index = modelUses.Count - 1; index >= 0; index--)
                await modelUses[index].DisposeAsync();
        }
    }

    private async Task<TcpTaskExecutionResult> ExecuteOfflineImageFolderAsync(
        TcpTaskExecutionRequest request, TaskEntity task, Recipe recipe, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(recipe.CameraDeviceId))
            throw new DirectoryNotFoundException($"图片目录不存在: {recipe.CameraDeviceId}");
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
        var files = Directory.EnumerateFiles(recipe.CameraDeviceId, "*.*", SearchOption.TopDirectoryOnly)
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
        var modelSessions = await GetModelSessionsAsync(recipe, cancellationToken);
        var modelUses = new List<AlgorithmSessionCache.SessionUseLease>();
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
        var processed = 0;
        run.RecordCompleted += (_, result) => { lastRecord = result; Interlocked.Increment(ref processed); };
        run.SourceCompleted += (_, _) => sourceCompleted.TrySetResult(true);
        run.Faulted += (_, fault) => faulted.TrySetResult(new InvalidOperationException($"{fault.Code}: {fault.Message}"));
        try
        {
            foreach (var session in modelSessions.Sop.Values
                .DistinctBy(session => session.SessionId)
                .OrderBy(session => session.SessionId, StringComparer.Ordinal))
                modelUses.Add(await services.AlgorithmSessions.AcquireUseAsync(session, cancellationToken));

            var batch = await new BatchService(services.Batches).ResumeOrStartAsync(
                task.Id, run.Counting, services.Records, services.Settings.DataDirectory, task.StationCode, cancellationToken);
            await run.StartAsync(recipe, task.Id, camera, modelSessions.Primary, batch.Id,
                FrameRoutingStrategy.Bounded, cancellationToken, projectId: request.ProjectCode,
                sopAlgorithms: recipe.Sop?.Definition is not null ? modelSessions.Sop : null);
            var signal = await Task.WhenAny(sourceCompleted.Task, faulted.Task).WaitAsync(cancellationToken);
            if (signal == faulted.Task) throw await faulted.Task;
            await sourceCompleted.Task.WaitAsync(cancellationToken);
            await run.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
            if (faulted.Task.IsCompleted) throw await faulted.Task;
            var skipped = (camera as IImageFolderCameraSession)?.SkippedFileCount ?? 0;
            if (lastRecord is null || processed + skipped != files.Length)
                throw new InvalidOperationException($"离线图片检测未完整完成：成功 {processed} 张，跳过 {skipped} 张，共 {files.Length} 张");
            await run.StopAsync();
            await new BatchService(services.Batches).EndAsync(run.Counting.State.CurrentTotal, "completed", cancellationToken);
            return new TcpTaskExecutionResult("completed", lastRecord.CountAfter, lastRecord.Decision.Status.ToString(), lastRecord.Record.Id);
        }
        finally
        {
            await run.DisposeAsync();
            for (var index = modelUses.Count - 1; index >= 0; index--)
                await modelUses[index].DisposeAsync();
        }
    }
    /// <summary>后台预热所有启用 TCP 触发的任务：提前完成算法 Worker 握手与模型加载，
    /// 使首次 TCP 触发直接复用热会话，避免 Python/TensorRT 冷启动挤占 30 秒执行超时。单个任务失败只记录日志。</summary>
    public async Task PrewarmConfiguredTasksAsync(CancellationToken cancellationToken = default)
    {
        var logger = services.LoggerFactory.CreateLogger<TcpTaskExecutionService>();
        try
        {
            var tasks = await services.Tasks.ListAsync(cancellationToken);
            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTcpTriggerEnabled(task.TriggerJson)) continue;
                try
                {
                    await PrewarmTaskAsync(task.Id, cancellationToken);
                    logger.LogInformation("TCP 任务模型预热完成: {Task} ({Station})", task.Name, task.StationCode);
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
    {
        var task = await services.Tasks.FindAsync(taskId, cancellationToken);
        if (task is null) return;
        var found = await services.Recipes.FindAsync(task.Id, cancellationToken);
        if (found?.Recipe is not { } recipe) return;

        await GetModelSessionsAsync(recipe, cancellationToken);
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

    private async Task<(IAlgorithmSession Primary, IReadOnlyDictionary<string, IAlgorithmSession> Sop)> GetModelSessionsAsync(
        Recipe recipe, CancellationToken cancellationToken)
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
            sessions[model.Id] = await services.AlgorithmSessions.GetOrInitializeAsync(
                model.Execution.PluginId,
                new AlgorithmInitialization
                {
                    Settings = DetectionRunService.BuildAlgorithmSettings(modelRecipe),
                    ExecutionProvider = model.Execution.ExecutionProvider,
                }, cancellationToken);
        }
        return (sessions.Values.First(), sessions);
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
