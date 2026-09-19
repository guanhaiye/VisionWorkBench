using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<long, ModelSessionPool> _models = new();
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

        var camera = _cameras.GetOrAdd(CameraKey(recipe), _ => new SharedCameraRuntime(services, recipe));
        await camera.EnsureStartedAsync(cancellationToken);
        var modelPool = _models.GetOrAdd(task.Id, _ => new ModelSessionPool(services, recipe, 3));
        var lease = await modelPool.RentAsync(cancellationToken);
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

        try
        {
            var batch = await new BatchService(services.Batches).ResumeOrStartAsync(
                task.Id, run.Counting, services.Records, services.Settings.DataDirectory, task.StationCode, cancellationToken);
            await run.StartAsync(recipe, task.Id, assignedCamera, lease.Session, batch.Id,
                FrameRoutingStrategy.Bounded, cancellationToken, projectId: request.ProjectCode);
            var result = await completion.Task.WaitAsync(cancellationToken);
            await run.StopAsync();
            await new BatchService(services.Batches).EndAsync(result.CountAfter, "completed", cancellationToken);
            return new TcpTaskExecutionResult("completed", result.CountAfter, result.Decision.Status.ToString(), result.Record.Id);
        }
        finally
        {
            await run.DisposeAsync();
            await modelPool.ReturnAsync(lease, lease.Session.State == AlgorithmSessionState.Ready);
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
            foreach (var pool in _models.Values)
                try { await pool.DisposeAsync(); } catch { }
            foreach (var camera in _cameras.Values)
                try { await camera.DisposeAsync(); } catch { }
            _models.Clear();
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
            foreach (var pool in _models.Values) await pool.DisposeAsync();
            foreach (var camera in _cameras.Values) await camera.DisposeAsync();
            _models.Clear();
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

    private sealed class ModelSessionPool : IAsyncDisposable
    {
        private readonly AppServices _services;
        private readonly Recipe _recipe;
        private readonly Channel<IAlgorithmSession> _available = Channel.CreateUnbounded<IAlgorithmSession>();
        private readonly SemaphoreSlim _leases;
        private readonly Lock _gate = new();
        private readonly List<IAlgorithmSession> _all = [];

        public ModelSessionPool(AppServices services, Recipe recipe, int maxSessions)
        {
            _services = services;
            _recipe = recipe;
            _leases = new SemaphoreSlim(Math.Max(1, maxSessions), Math.Max(1, maxSessions));
        }

        public async Task<Lease> RentAsync(CancellationToken cancellationToken)
        {
            await _leases.WaitAsync(cancellationToken);
            if (_available.Reader.TryRead(out var available)) return new Lease(available);
            try
            {
                var session = await _services.AlgorithmManager.CreateSessionAsync(_recipe.PluginId, cancellationToken);
                lock (_gate) _all.Add(session);
                return new Lease(session);
            }
            catch
            {
                _leases.Release();
                throw;
            }
        }

        public async ValueTask ReturnAsync(Lease lease, bool reusable)
        {
            if (reusable) _available.Writer.TryWrite(lease.Session);
            else
            {
                lock (_gate) _all.Remove(lease.Session);
                await lease.Session.DisposeAsync();
            }
            _leases.Release();
        }

        public async ValueTask DisposeAsync()
        {
            _available.Writer.TryComplete();
            IAlgorithmSession[] sessions;
            lock (_gate) sessions = [.. _all];
            foreach (var session in sessions) await session.DisposeAsync();
            _leases.Dispose();
        }

        public sealed class Lease(IAlgorithmSession session)
        {
            public IAlgorithmSession Session { get; } = session;
        }
    }
}
