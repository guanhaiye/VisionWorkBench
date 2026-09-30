using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public enum DetectionRunState
{
    Idle,
    Running,
    Paused,
    Stopped,
    Faulted,
}

public sealed class RecordCompletedEventArgs : EventArgs
{
    public string StationCode { get; init; } = "";
    public required InspectionRecordEntity Record { get; init; }
    /// <summary>本次结果对应的原始帧，供界面将原图与结果叠加显示。</summary>
    public VideoFrame? Frame { get; init; }
    public required AlgorithmOutput Output { get; init; }
    public required DecisionResult Decision { get; init; }
    public SopSnapshot? Sop { get; init; }
    public long CountAfter { get; init; }
}

public sealed class RunFaultedEventArgs : EventArgs
{
    public required string Source { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 检测运行时编排：相机帧 → 帧调度 → 算法推理 → 规则判定 → 计数 → 落库（文档 §11/§8.4）。
/// 落库走后台写队列，不阻塞检测循环（PER-007）；NG/待确认保存原图+标注图（DAT-002）。
/// </summary>
public sealed class DetectionRunService : IAsyncDisposable
{
    private readonly RecordRepository _records;
    private readonly TempImageStore _tempStore;
    private readonly ILogger<DetectionRunService>? _logger;
    private readonly IResultPublisher? _publisher;
    private readonly Func<bool> _shouldPersist;
    private readonly Func<bool> _shouldSaveFullImages;
    private readonly PythonPostProcessService _pythonPostProcess;
    private readonly SopRunRepository? _sopRuns;
    private readonly ISopPendingReplayTrigger? _pendingReplayTrigger;
    private readonly Channel<Func<CancellationToken, Task>> _dbWrites =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly string _counterId;
    private readonly object _singleCaptureLock = new();
    private readonly object _sopGate = new();
    private readonly SemaphoreSlim _sopCycleGate = new(1, 1);
    private readonly HashSet<string> _activeSopFinalizations = new(StringComparer.Ordinal);
    private string? _lastCycleSwitchCycleId;
    private Task? _dbLoopTask;
    private long _pendingDbWrites;
    private Task? _processingLoopTask;
    private Task? _sopClockTask;
    private TaskCompletionSource<RecordCompletedEventArgs?>? _singleCaptureRequest;
    private FrameRoutingStrategy _routingStrategy = FrameRoutingStrategy.LatestOnly;

    public DetectionRunState State { get; private set; } = DetectionRunState.Idle;
    public FrameScheduler Scheduler { get; private set; } = new();
    public CountingService Counting { get; }
    public long? BatchId { get; private set; }

    /// <summary>更新当前实时检测区域；null 表示恢复全图检测。</summary>
    public void UpdateRoi(NormalizedRect? roi)
    {
        if (State is DetectionRunState.Idle or DetectionRunState.Stopped or DetectionRunState.Faulted)
        {
            return;
        }
        _recipe = _recipe with { Roi = roi };
    }

    private ICameraSession? _camera;
    private IAlgorithmSession? _algorithm;
    private IReadOnlyDictionary<string, IAlgorithmSession> _sopAlgorithms =
        new Dictionary<string, IAlgorithmSession>(StringComparer.Ordinal);
    private Recipe _recipe = null!;
    private long _taskId;
    private string _projectId = "default";
    private string _evidenceDir = "";
    private volatile bool _paused;
    private int _disposed;
    private BehaviorEngine? _behaviorEngine;
    private SopStateMachine? _sopStateMachine;
    private long? _sopRunId;
    private string? _sopCycleId;

    public SopSnapshot? Sop => _sopStateMachine?.Snapshot;
    public long? SopRunId => _sopRunId;
    public string? SopCycleId => _sopCycleId;

    public event EventHandler<RecordCompletedEventArgs>? RecordCompleted;
    /// <summary>SOP 状态变化，包括无新算法帧时由独立超时钟触发的变化。</summary>
    public event Action<SopSnapshot>? SopChanged;
    public event EventHandler<RunFaultedEventArgs>? Faulted;
    public event EventHandler? SourceCompleted;
    public event EventHandler<DetectionRunState>? StateChanged;

    /// <summary>预览帧 tap（相机线程回调；UI 订阅方必须自行 Dispatcher 编组）。</summary>
    public event EventHandler<VideoFrame>? PreviewReceived;

    private sealed record ModelSession(Recipe Recipe, IAlgorithmSession Session);

    public DetectionRunService(
        RecordRepository records,
        TempImageStore tempStore,
        ILogger<DetectionRunService>? logger = null,
        string? counterId = null,
        IResultPublisher? publisher = null,
        Func<bool>? shouldPersist = null,
        Func<bool>? shouldSaveFullImages = null,
        string? pythonExecutable = null,
        SopRunRepository? sopRuns = null,
        ISopPendingReplayTrigger? pendingReplayTrigger = null)
    {
        _records = records;
        _tempStore = tempStore;
        _logger = logger;
        _publisher = publisher;
        _shouldPersist = shouldPersist ?? (() => true);
        // 保持既有的数据策略：默认只为 NG/待复核结果保存证据图。
        // “保存全部原图”必须由调用方显式开启，否则会悄悄增加磁盘占用，
        // 也会让 OK 记录违反只保存结构化结果的约定。
        _shouldSaveFullImages = shouldSaveFullImages ?? (() => false);
        _pythonPostProcess = new PythonPostProcessService(pythonExecutable, logger);
        _sopRuns = sopRuns;
        _pendingReplayTrigger = pendingReplayTrigger;
        _counterId = counterId ?? "default";
        Counting = new CountingService(_counterId);
        // 落库循环随服务启动（人工修正可在未开始检测时使用），随 DisposeAsync 结束
        _dbLoopTask = Task.Run(DbWriteLoopAsync);
    }

    /// <summary>开始检测：接线相机帧、启动推理循环与后台落库循环。</summary>
    /// <param name="routingStrategy">
    /// 实时相机默认 LatestOnly（推理慢时丢旧帧，FRM-002）；
    /// 有限源（图片目录批次/视频复现）用 Bounded 逐帧不漏（FRM-003）。
    /// </param>
    public async Task StartAsync(
        Recipe recipe,
        long taskId,
        ICameraSession camera,
        IAlgorithmSession algorithm,
        long? batchId = null,
        FrameRoutingStrategy routingStrategy = FrameRoutingStrategy.LatestOnly,
        CancellationToken cancellationToken = default,
        string? projectId = null,
        bool startProcessingLoop = true,
        IReadOnlyDictionary<string, IAlgorithmSession>? sopAlgorithms = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(algorithm);
        if (State is DetectionRunState.Running or DetectionRunState.Paused)
        {
            throw new InvalidOperationException("检测已在运行，先停止再开始");
        }
        _recipe = string.IsNullOrWhiteSpace(recipe.StationCode)
            ? recipe with { StationCode = $"ST-{taskId:000}" }
            : recipe with { StationCode = recipe.StationCode.Trim() };
        _taskId = taskId;
        _projectId = string.IsNullOrWhiteSpace(projectId) ? "default" : projectId.Trim();
        _camera = camera;
        _algorithm = algorithm;
        _sopAlgorithms = sopAlgorithms ?? new Dictionary<string, IAlgorithmSession>(StringComparer.Ordinal);
        _routingStrategy = routingStrategy;
        BatchId = batchId;
        _behaviorEngine = new BehaviorEngine(recipe.Behavior);
        _sopRunId = null;
        _sopCycleId = null;
        _lastCycleSwitchCycleId = null;
        await TriggerPendingReplayAsync(cancellationToken);
        await StartSopCycleAsync(DateTimeOffset.UtcNow);
        _evidenceDir = Path.Combine(
            Path.GetDirectoryName(_tempStore.RootDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? ".",
            "evidence", DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(_evidenceDir);

        // 普通任务只启动一个会话；SOP 任务按步骤启动各自模型会话。
        var sessions = _sopAlgorithms.Count == 0
            ? [new ModelSession(recipe, algorithm)]
            : _sopAlgorithms
                .Select(pair => new ModelSession(FindStepRecipe(recipe, pair.Key), pair.Value))
                .DistinctBy(item => item.Session, ReferenceEqualityComparer.Instance)
                .ToArray();
        foreach (var model in sessions)
        {
            if (model.Session.State == AlgorithmSessionState.Uninitialized)
            {
                await model.Session.InitializeAsync(new AlgorithmInitialization
                {
                    Settings = BuildAlgorithmSettings(model.Recipe),
                    ExecutionProvider = model.Recipe.ExecutionProvider,
                }, cancellationToken);
            }
            else if (model.Session.State == AlgorithmSessionState.Running)
            {
                // Cached sessions outlive individual detection runs so the loaded model
                // can be reused. Recover a session left running by the previous run
                // before starting the next one; stop_session preserves worker/model state.
                await model.Session.StopAsync(cancellationToken);
            }
            else if (model.Session.State != AlgorithmSessionState.Ready)
            {
                throw new InvalidOperationException($"算法会话未就绪（当前状态：{model.Session.State}）");
            }
            await model.Session.StartAsync(new AlgorithmStartOptions
            {
                Mode = "stream",
                Roi = model.Recipe.Roi,
            }, cancellationToken);
        }

        // 帧调度器接线
        Scheduler = new FrameScheduler(routingStrategy);
        Scheduler.PreviewReceived += (s, frame) => PreviewReceived?.Invoke(s, frame);
        camera.FrameReceived += OnCameraFrame;
        camera.Completed += OnCameraCompleted;
        camera.Faulted += OnCameraFaulted;
        foreach (var session in sessions.Select(item => item.Session))
        {
            session.Faulted += OnAlgorithmFaulted;
        }
        await camera.StartAsync(cancellationToken);

        _paused = false;
        SetState(DetectionRunState.Running);
        StartSopClock();
        if (startProcessingLoop)
        {
            _processingLoopTask = Task.Run(() => ProcessingLoopAsync(_cts.Token));
        }
        _logger?.LogInformation("检测开始: task={TaskId} batch={BatchId} counter={Counter}",
            taskId, batchId, _counterId);
    }

    private async Task TriggerPendingReplayAsync(CancellationToken cancellationToken)
    {
        if (_pendingReplayTrigger is null)
        {
            return;
        }

        await _pendingReplayTrigger.TriggerAsync(cancellationToken);
    }

    private async Task StartSopCycleAsync(DateTimeOffset startedAt)
    {
        await _sopCycleGate.WaitAsync();
        try
        {
            await StartSopCycleCoreAsync(startedAt);
        }
        finally
        {
            _sopCycleGate.Release();
        }
    }

    private async Task StartSopCycleCoreAsync(DateTimeOffset startedAt)
    {
        if (_recipe.Sop?.Definition is not { } definition)
        {
            lock (_sopGate)
            {
                _sopStateMachine = null;
                _sopRunId = null;
                _sopCycleId = null;
            }
            return;
        }

        var machine = new SopStateMachine(definition, _recipe.Sop.RunMode);
        var started = machine.Start(startedAt);
        var cycleId = $"cycle-{Guid.NewGuid():N}";
        long? runId = null;
        if (_sopRuns is not null && _shouldPersist())
        {
            var snapshotJson = JsonSerializer.Serialize(definition, JsonOpts);
            var run = await _sopRuns.StartAsync(new SopRunEntity
            {
                SopDefinitionId = _recipe.Sop.DefinitionId,
                SopVersion = _recipe.Sop.Version,
                DefinitionHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshotJson))),
                DefinitionSnapshotJson = snapshotJson,
                ProjectId = _projectId,
                StationCode = _recipe.StationCode,
                TaskId = _taskId,
                BatchId = BatchId,
                CycleId = cycleId,
                Status = started.Snapshot.Status.ToString(),
                CurrentStepOrder = started.Snapshot.CurrentStepOrder,
                StartedAtUtc = startedAt.UtcDateTime,
            });
            runId = run.Id;
        }

        lock (_sopGate)
        {
            _sopStateMachine = machine;
            _sopRunId = runId;
            _sopCycleId = cycleId;
        }
        SopChanged?.Invoke(started.Snapshot);
    }

    /// <summary>当前 SOP 产品已进入终态后，手动开始下一件产品，创建全新的周期和运行记录。</summary>
    public async Task<SopSnapshot?> StartNextProductAsync()
    {
        if (State is not (DetectionRunState.Running or DetectionRunState.Paused))
        {
            throw new InvalidOperationException("检测尚未运行，不能开始下一件产品。");
        }

        string? observedCycleId;
        SopRunStatus observedStatus;
        lock (_sopGate)
        {
            if (_sopStateMachine is null)
            {
                throw new InvalidOperationException("当前任务未绑定 SOP。");
            }
            observedCycleId = _sopCycleId;
            observedStatus = _sopStateMachine.Snapshot.Status;
        }

        await _sopCycleGate.WaitAsync();
        try
        {
            lock (_sopGate)
            {
                // 第二个并发调用观察到的是旧周期；第一个调用已切换成功时直接返回新周期，
                // 从而把按钮连点/重复请求变成幂等操作。
                if (!string.Equals(observedCycleId, _sopCycleId, StringComparison.Ordinal))
                {
                    return _sopStateMachine?.Snapshot;
                }
                if (_sopStateMachine is null)
                {
                    throw new InvalidOperationException("当前产品尚未完成或失败，不能直接开始下一件产品；如需放弃请点击复位当前产品。");
                }
                if (!IsSopTerminal(observedStatus) || !IsSopTerminal(_sopStateMachine.Snapshot.Status))
                {
                    if (string.Equals(_lastCycleSwitchCycleId, observedCycleId, StringComparison.Ordinal))
                    {
                        return _sopStateMachine.Snapshot;
                    }
                    throw new InvalidOperationException("当前产品尚未完成或失败，不能直接开始下一件产品；如需放弃请点击复位当前产品。");
                }
            }

            await TriggerPendingReplayAsync(CancellationToken.None);
            await StartSopCycleCoreAsync(DateTimeOffset.UtcNow);
            _lastCycleSwitchCycleId = _sopCycleId;
            return Sop;
        }
        finally
        {
            _sopCycleGate.Release();
        }
    }

    /// <summary>放弃当前产品并立即创建一个新的 SOP 产品周期；不停止相机和算法会话。</summary>
    public async Task<SopSnapshot?> ResetProductAsync()
    {
        if (State is not (DetectionRunState.Running or DetectionRunState.Paused))
        {
            throw new InvalidOperationException("检测尚未运行，不能复位产品周期。");
        }

        string? observedCycleId;
        lock (_sopGate)
        {
            if (_sopStateMachine is null)
            {
                throw new InvalidOperationException("当前任务未绑定 SOP。");
            }
            observedCycleId = _sopCycleId;
        }

        await _sopCycleGate.WaitAsync();
        try
        {
            lock (_sopGate)
            {
                if (!string.Equals(observedCycleId, _sopCycleId, StringComparison.Ordinal))
                {
                    return _sopStateMachine?.Snapshot;
                }
            }

            SopTransition? aborted = null;
            long? runId = null;
            string? abortedCycleId = null;
            lock (_sopGate)
            {
                if (_sopStateMachine is { } machine && !IsSopTerminal(machine.Snapshot.Status))
                {
                    aborted = machine.Abort("人工复位当前产品", interrupted: true);
                    runId = _sopRunId;
                    abortedCycleId = _sopCycleId;
                }
            }
            if (aborted is { } transition)
            {
                await PersistSopSnapshotAsync(runId, transition.Snapshot);
                SopChanged?.Invoke(transition.Snapshot);
                QueueProductFinalization(runId, abortedCycleId, transition.Snapshot, decision: null);
            }

            await TriggerPendingReplayAsync(CancellationToken.None);
            await StartSopCycleCoreAsync(DateTimeOffset.UtcNow);
            _lastCycleSwitchCycleId = _sopCycleId;
            return Sop;
        }
        finally
        {
            _sopCycleGate.Release();
        }
    }

    private void StartSopClock()
    {
        if (_sopStateMachine is null || _sopClockTask is not null)
        {
            return;
        }

        _sopClockTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    // 统一采用“暂停即暂停 SOP 计时”的策略；恢复后继续原步骤计时。
                    if (State != DetectionRunState.Running)
                    {
                        continue;
                    }

                    SopTransition transition;
                    long? runId;
                    string? cycleId;
                    lock (_sopGate)
                    {
                        if (_sopStateMachine is null || IsSopTerminal(_sopStateMachine.Snapshot.Status))
                        {
                            continue;
                        }
                        transition = _sopStateMachine.AdvanceTime(DateTimeOffset.UtcNow);
                        runId = _sopRunId;
                        cycleId = _sopCycleId;
                    }
                    if (!transition.Changed)
                    {
                        continue;
                    }

                    await PersistSopSnapshotAsync(runId, transition.Snapshot);
                    SopChanged?.Invoke(transition.Snapshot);
                    if (transition.IsTerminal)
                    {
                        QueueProductFinalization(runId, cycleId, transition.Snapshot, decision: null);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // 正常停止。
            }
        });
    }

    private async Task PersistSopSnapshotAsync(long? runId, SopSnapshot snapshot, long? inspectionRecordId = null)
    {
        if (runId is { } id && _sopRuns is not null && _shouldPersist())
        {
            await _sopRuns.SaveSnapshotAsync(id, snapshot, inspectionRecordId);
        }
    }

    /// <summary>替换单次测试输入源，但保留当前算法会话和已加载模型。</summary>
    public async Task RestartCameraAsync(
        ICameraSession camera,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_algorithm is null || State is DetectionRunState.Idle or DetectionRunState.Stopped
            or DetectionRunState.Faulted)
        {
            throw new InvalidOperationException("当前检测会话不可复用");
        }

        if (_camera is not null)
        {
            var old = _camera;
            DetachCamera(old);
            await old.StopAsync(CancellationToken.None);
            await old.DisposeAsync();
        }

        _camera = camera;
        Scheduler = new FrameScheduler(_routingStrategy);
        Scheduler.PreviewReceived += (s, frame) => PreviewReceived?.Invoke(s, frame);
        camera.FrameReceived += OnCameraFrame;
        camera.Completed += OnCameraCompleted;
        camera.Faulted += OnCameraFaulted;
        await camera.StartAsync(cancellationToken);

        _paused = false;
        SetState(DetectionRunState.Running);
        _processingLoopTask = Task.Run(() => ProcessingLoopAsync(_cts.Token));
    }

    private readonly object _pauseSync = new();

    public async Task PauseAsync()
    {
        if (State == DetectionRunState.Running)
        {
            lock (_pauseSync)
            {
                _paused = true;
                Scheduler.ClearPendingFrames();
            }
            if (_camera is not null)
            {
                await _camera.PauseAsync(CancellationToken.None);
            }
            lock (_sopGate)
            {
                _sopStateMachine?.Pause(DateTimeOffset.UtcNow);
            }
            SetState(DetectionRunState.Paused);
        }
    }

    public async Task ResumeAsync()
    {
        if (State == DetectionRunState.Paused)
        {
            lock (_pauseSync)
            {
                _paused = false;
            }
            lock (_sopGate)
            {
                _sopStateMachine?.Resume(DateTimeOffset.UtcNow);
            }
            SetState(DetectionRunState.Running);
            if (_camera is not null)
            {
                await _camera.ResumeAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>重建相机会话并恢复采集，保留算法会话和已持久化计数。</summary>
    public async Task<ICameraSession?> ReconnectCameraAsync(
        Func<CancellationToken, Task<ICameraSession>> createOpenedSession,
        int maxAttempts = 5,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createOpenedSession);
        if (_camera is null || State is DetectionRunState.Stopped or DetectionRunState.Idle)
        {
            return null;
        }

        var old = _camera;
        DetachCamera(old);
        try
        {
            await old.StopAsync(CancellationToken.None);
            await old.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "释放断线相机会话失败");
        }
        _camera = null;

        var attempts = Math.Max(1, maxAttempts);
        var delay = retryDelay ?? TimeSpan.FromSeconds(1);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ICameraSession? replacement = null;
            try
            {
                replacement = await createOpenedSession(cancellationToken);
                if (replacement.State == CameraSessionState.Faulted)
                {
                    await replacement.DisposeAsync();
                    throw new InvalidOperationException("相机会话打开后仍处于故障状态");
                }

                replacement.FrameReceived += OnCameraFrame;
                replacement.Completed += OnCameraCompleted;
                replacement.Faulted += OnCameraFaulted;
                _camera = replacement;
                await replacement.StartAsync(cancellationToken);
                if (replacement.State == CameraSessionState.Faulted)
                {
                    DetachCamera(replacement);
                    await replacement.DisposeAsync();
                    _camera = null;
                    throw new InvalidOperationException("相机会话启动后仍处于故障状态");
                }
                SetState(_paused ? DetectionRunState.Paused : DetectionRunState.Running);
                _logger?.LogInformation("相机重连成功，尝试次数 {Attempt}", attempt);
                return replacement;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (replacement is not null && ReferenceEquals(_camera, replacement))
                {
                    DetachCamera(replacement);
                    _camera = null;
                    try
                    {
                        await replacement.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        _logger?.LogDebug(disposeEx, "清理失败的重连会话失败");
                    }
                }
                _logger?.LogWarning(ex, "相机重连失败，尝试 {Attempt}/{MaxAttempts}", attempt, attempts);
                if (attempt < attempts)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        SetState(DetectionRunState.Faulted);
        return null;
    }

    /// <summary>
    /// 单次检测：独立单次会话直接取一帧；连续会话向推理循环申请一帧，
    /// 避免与连续采集并发消费同一调度器。
    /// </summary>
    public async Task<RecordCompletedEventArgs?> SubmitSingleAsync(TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (State is DetectionRunState.Idle or DetectionRunState.Stopped or DetectionRunState.Faulted)
        {
            return null;
        }

        // 独立单次会话没有后台处理循环，由调用方直接消费调度器。
        if (_processingLoopTask is null)
        {
            return await SubmitSingleDirectAsync(timeout, cancellationToken);
        }

        var request = new TaskCompletionSource<RecordCompletedEventArgs?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_singleCaptureLock)
        {
            if (_singleCaptureRequest is not null)
            {
                return null;
            }
            _singleCaptureRequest = request;
        }

        using var singleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t)
        {
            singleCts.CancelAfter(t);
        }
        try
        {
            return await request.Task.WaitAsync(singleCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_singleCaptureLock)
            {
                if (ReferenceEquals(_singleCaptureRequest, request))
                {
                    _singleCaptureRequest = null;
                }
            }
        }
    }

    private async Task<RecordCompletedEventArgs?> SubmitSingleDirectAsync(
        TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var singleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t)
        {
            singleCts.CancelAfter(t);
        }
        try
        {
            var frame = await Scheduler.TakeNextAsync(singleCts.Token);
            return frame is null ? null : await ProcessFrameAsync(frame);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>等待有限输入源的推理队列处理完毕，不主动取消在途帧。</summary>
    public async Task WaitForCompletionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var processing = _processingLoopTask;
        if (processing is null) return;
        if (timeout is { } limit)
            await processing.WaitAsync(limit, cancellationToken);
        else
            await processing.WaitAsync(cancellationToken);
    }

    /// <summary>停止检测（不结束批次；批次由 BatchService 管理）。</summary>
    public async Task StopAsync()
    {
        if (State is DetectionRunState.Idle or DetectionRunState.Stopped)
        {
            return;
        }
        SopTransition? stoppedSop = null;
        long? stoppedSopRunId = null;
        string? stoppedSopCycleId = null;
        lock (_sopGate)
        {
            if (_sopStateMachine is { } sop && !IsSopTerminal(sop.Snapshot.Status))
            {
                stoppedSop = sop.Abort("检测已停止", interrupted: true);
                stoppedSopRunId = _sopRunId;
                stoppedSopCycleId = _sopCycleId;
            }
        }
        if (stoppedSop is { } stoppedTransition)
        {
            await PersistSopSnapshotAsync(stoppedSopRunId, stoppedTransition.Snapshot);
            SopChanged?.Invoke(stoppedTransition.Snapshot);
            QueueProductFinalization(stoppedSopRunId, stoppedSopCycleId, stoppedTransition.Snapshot, decision: null);
        }
        SetState(DetectionRunState.Stopped);
        CompleteSingleCapture(null);
        _cts.Cancel();
        if (_sopClockTask is not null)
        {
            try
            {
                await _sopClockTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // 超时钟退出失败不阻塞相机和算法停止。
            }
            _sopClockTask = null;
        }

        if (_camera is not null)
        {
            var camera = _camera;
            DetachCamera(camera);
            await camera.StopAsync(CancellationToken.None);
        }
        foreach (var session in AlgorithmSessions())
        {
            session.Faulted -= OnAlgorithmFaulted;
        }
        if (_processingLoopTask is not null)
        {
            try
            {
                await _processingLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 循环自行退出
            }
        }
        foreach (var session in AlgorithmSessions())
        {
            try
            {
                await session.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "停止算法会话失败");
            }
        }
        // 停止返回前等落库排空：调用方随后查询/结束批次必须能看到全部记录（DAT-001）
        await WaitDbWritesAsync(TimeSpan.FromSeconds(5));
        _logger?.LogInformation("检测停止: counter={Counter} total={Total} dropped={Dropped}",
            _counterId, Counting.State.CurrentTotal, Scheduler.DroppedFrameCount);
    }

    private IEnumerable<IAlgorithmSession> AlgorithmSessions()
        => new[] { _algorithm }
            .Where(session => session is not null)
            .Cast<IAlgorithmSession>()
            .Concat(_sopAlgorithms.Values)
            .Distinct();

    /// <summary>人工修正计数（CNT-S-010）：原因必填，事件落库。</summary>
    public async Task<CountingAdjustment> AdjustCountAsync(long delta, string reason, string @operator)
    {
        var adjustment = Counting.Adjust(delta, reason, @operator);
        EnqueueWrite(ct => _records.AppendCountingEventAsync(adjustment.Event, BatchId, ct));
        _logger?.LogInformation("人工修正计数: {Before} → {After}（{Reason}, {Operator}）",
            adjustment.Before, adjustment.After, reason, @operator);
        await Task.CompletedTask;
        return adjustment;
    }

    /// <summary>
    /// 计数清零（CNT-S-010 / CNT-L-012 唯一入口）：宿主累计清零 + CounterReset 落库，
    /// 流水模式再 best-effort 通知 worker 清除轨迹与去重记忆（失败仅告警，不阻断）。
    /// </summary>
    public async Task<CountingAdjustment> ResetCountAsync(string reason, string @operator)
    {
        var adjustment = Counting.Reset(reason, @operator);
        EnqueueWrite(ct => _records.AppendCountingEventAsync(adjustment.Event, BatchId, ct));
        if (_algorithm is not null && _recipe.CountingMode != CountingMode.Snapshot)
        {
            try
            {
                await _algorithm.SendCommandAsync(
                    MessageType.CounterCommand,
                    new { command = "reset" },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "通知 worker 计数清零失败（尽力而为，CNT-L-012）");
            }
        }
        return adjustment;
    }

    // ---- 内部：相机事件 → 调度器 ----

    private void OnCameraFrame(object? sender, VideoFrameReceivedEventArgs e)
    {
        lock (_pauseSync)
        {
            if (_paused || State is DetectionRunState.Paused or DetectionRunState.Stopped)
            {
                return;
            }
            Scheduler.OnFrame(e.Frame);
        }
    }

    private void DetachCamera(ICameraSession camera)
    {
        camera.FrameReceived -= OnCameraFrame;
        camera.Completed -= OnCameraCompleted;
        camera.Faulted -= OnCameraFaulted;
    }

    private void OnCameraCompleted(object? sender, EventArgs e)
    {
        Scheduler.Complete();
        SourceCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnCameraFaulted(object? sender, CameraFaultedEventArgs e)
    {
        CompleteSingleCapture(null);
        SetState(DetectionRunState.Faulted);
        Faulted?.Invoke(this, new RunFaultedEventArgs
        {
            Source = "camera",
            Code = e.Fault.Code,
            Message = e.Fault.Message,
        });
    }

    private void OnAlgorithmFaulted(object? sender, AlgorithmFaultedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0
            || State is DetectionRunState.Stopped or DetectionRunState.Idle)
        {
            return;
        }
        CompleteSingleCapture(null);
        SetState(DetectionRunState.Faulted);
        _cts.Cancel();
        Faulted?.Invoke(this, new RunFaultedEventArgs
        {
            Source = "algorithm",
            Code = e.ErrorCode,
            Message = e.Message,
        });
    }

    // ---- 内部：推理循环 ----

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        // 退出条件：源播完（null）或空闲等待时被取消；已入队的帧在取消后仍要处理完
        // （有限源逐帧不漏，FRM-003；Stop 只阻止取新帧，在途帧必须完成）
        while (true)
        {
            VideoFrame? frame;
            try
            {
                frame = await Scheduler.TakeNextAsync(ct);
            }
            catch (OperationCanceledException)
            {
                CompleteSingleCapture(null);
                break;
            }
            if (frame is null)
            {
                // 有限源播完
                _logger?.LogInformation("输入源播放完毕，检测循环退出");
                CompleteSingleCapture(null);
                break;
            }
            if (_paused && !HasSingleCaptureRequest())
            {
                // 暂停即丢帧（CNT-D-007 既定政策）：worker 无输入不产事件也不清零；
                // 恢复后暂停期间位移大的目标可能因 IoU 失配重建 ID，由去重窗口缓解。
                continue;
            }
            try
            {
                var completed = await ProcessFrameAsync(frame);
                CompleteSingleCapture(completed);
            }
            catch (AlgorithmFaultException ex)
            {
                CompleteSingleCapture(null);
                SetState(DetectionRunState.Faulted);
                Faulted?.Invoke(this, new RunFaultedEventArgs
                {
                    Source = "algorithm",
                    Code = ex.ErrorCode,
                    Message = ex.Message,
                });
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CompleteSingleCapture(null);
                break;
            }
            catch (Exception ex)
            {
                CompleteSingleCapture(null);
                _logger?.LogWarning(ex, "处理帧失败，跳过 sequence={Seq}", frame.Sequence);
                // 不能只记录日志后继续丢帧，否则实时页面会一直显示预览，用户看不到
                // 任何结果也不知道推理链路已经失败。统一通知界面显示故障原因。
                SetState(DetectionRunState.Faulted);
                Faulted?.Invoke(this, new RunFaultedEventArgs
                {
                    Source = "algorithm",
                    Code = WorkerErrorCodes.InferenceFailed,
                    Message = ex.Message,
                });
                break;
            }
        }
        if (_sopStateMachine is { } sop && !IsSopTerminal(sop.Snapshot.Status))
        {
            var transition = sop.Abort("输入源结束，产品周期未完成", interrupted: true);
            if (_sopRunId is { } runId && _sopRuns is not null)
            {
                await _sopRuns.SaveSnapshotAsync(runId, transition.Snapshot);
            }
            QueueProductFinalization(_sopRunId, _sopCycleId, transition.Snapshot, decision: null);
        }
        if (State == DetectionRunState.Running)
        {
            SetState(DetectionRunState.Idle);
        }
    }

    private bool HasSingleCaptureRequest()
    {
        lock (_singleCaptureLock)
        {
            return _singleCaptureRequest is not null;
        }
    }

    private void CompleteSingleCapture(RecordCompletedEventArgs? result)
    {
        lock (_singleCaptureLock)
        {
            _singleCaptureRequest?.TrySetResult(result);
        }
    }

    private async Task<RecordCompletedEventArgs> ProcessFrameAsync(VideoFrame frame)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tempPath = _tempStore.SaveFrame(frame);
        try
        {
            var activeModel = GetActiveModel();
            var activeRecipe = activeModel.Recipe;
            var output = await activeModel.Session.SubmitAsync(new AlgorithmInput
            {
                InputId = $"in-{frame.Sequence}",
                ImagePath = tempPath,
                FrameSequence = frame.Sequence,
                CapturedAt = frame.Timestamp,
                Roi = activeRecipe.Roi,
            }, CancellationToken.None); // 在途帧必须完成（Stop 只阻止取新帧，不打断推理）

            var behavior = activeRecipe.TaskType.IsBehavior()
                ? _behaviorEngine?.Process(
                    BehaviorFrame.FromAlgorithmOutput(_recipe.CameraDeviceId, output, frame.Sequence, frame.Timestamp))
                : null;
            if (behavior is { Events.Count: > 0 })
            {
                output = output with { Events = [.. output.Events, .. behavior.Events] };
            }

            // Python 后处理可以修改输出和事件；必须先完成后处理，再把最终 output 送入 SOP。
            var decision = RuleEngine.Evaluate(output, activeRecipe.Rules, activeRecipe.Roi, activeRecipe.RoiPolicy);
            if (activeRecipe.PostProcess.Mode == PostProcessMode.PythonScript
                && !string.IsNullOrWhiteSpace(activeRecipe.PostProcess.Script))
            {
                var postProcess = await _pythonPostProcess.ExecuteAsync(
                    activeRecipe.PostProcess.Script,
                    tempPath,
                    output,
                    activeRecipe.TaskType,
                    CancellationToken.None);
                output = postProcess.Output;
                decision = postProcess.Decision;
            }
            // 算法在完整画面上推理，ROI 在宿主侧按边界策略过滤，避免小区域裁剪导致漏检。
            output = RoiFilter.ApplyToOutput(output, activeRecipe.Roi, activeRecipe.RoiPolicy);
            var sopResult = ApplySopEvents(output);
            var sopSnapshot = sopResult.Snapshot;
            var sopRunId = sopResult.RunId;
            decision = CombineDecision(decision, sopSnapshot);
            var countingEvents = Counting.ApplyOutput(
                output, snapshotFallback: _recipe.CountingMode == CountingMode.Snapshot);
            // 快照模式的普通模型由宿主补齐计数事件，确保界面累计、持久化和批次恢复一致。
            if (!ReferenceEquals(countingEvents, output.CountingEvents))
            {
                output = output with { CountingEvents = countingEvents };
            }
            sw.Stop();

            // 证据图（DAT-002）：NG/待确认保存原图+标注图，OK 只存结构化数据
            string? origPath = null;
            string? annotPath = null;
            var shouldPersist = _shouldPersist();
            if (shouldPersist && (_shouldSaveFullImages()
                                  || decision.Status is (DecisionStatus.Ng or DecisionStatus.ReviewRequired)))
            {
                var evidencePaths = EvidenceImagePathFactory.CreatePair(_evidenceDir, frame.Sequence);
                origPath = evidencePaths.OriginalImagePath;
                annotPath = evidencePaths.AnnotatedImagePath;
                System.IO.File.WriteAllBytes(origPath, System.IO.File.ReadAllBytes(tempPath));
                ImageAnnotator.AnnotateToFile(frame, output, decision, annotPath);
            }

            var rawEnvelope = new ResultEnvelope
            {
                ProjectId = _projectId,
                StationCode = _recipe.StationCode,
                TaskId = _taskId,
                BatchId = BatchId,
                Timestamp = output.Timestamp,
                Output = output,
            };
            var finalEnvelope = rawEnvelope with { Decision = decision };
            var record = new InspectionRecordEntity
            {
                ProjectId = _projectId,
                StationCode = _recipe.StationCode,
                TaskId = _taskId,
                BatchId = BatchId,
                StartedAt = startedAt.UtcDateTime,
                CompletedAt = DateTime.UtcNow,
                Status = ToStatusString(decision.Status),
                OriginalImagePath = origPath,
                AnnotatedImagePath = annotPath,
                AlgorithmElapsedMs = output.Performance?.TotalMs ?? 0,
                TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
                RawResultJson = JsonSerializer.Serialize(rawEnvelope, JsonOpts),
                FinalResultJson = JsonSerializer.Serialize(finalEnvelope, JsonOpts),
                WorkflowResultJson = sopSnapshot is null ? null : JsonSerializer.Serialize(sopSnapshot, JsonOpts),
                SopRunId = sopRunId,
            };

            if (shouldPersist)
            {
                EnqueueWrite(async ct =>
                {
                    var saved = await _records.AddAsync(record, output.CountingEvents, output.Events, ct);
                    if (sopRunId is { } runId && _sopRuns is not null && sopSnapshot is not null)
                    {
                        var publishedEnvelope = finalEnvelope with { RecordId = saved.Id };
                        if (sopResult.TerminalReached)
                        {
                            await FinalizeProductAsync(
                                runId,
                                sopResult.CycleId,
                                sopSnapshot,
                                decision,
                                saved.Id,
                                ct);
                        }
                        else
                        {
                            await _sopRuns.SaveSnapshotAsync(runId, sopSnapshot, saved.Id, ct);
                        }
                    }
                    if (_publisher is not null)
                    {
                        await _publisher.PublishAsync(finalEnvelope with { RecordId = saved.Id }, ct);
                    }
                });
            }
            else if (_publisher is not null || (sopResult.TerminalReached && sopSnapshot is not null))
            {
                EnqueueWrite(async ct =>
                {
                    if (sopResult.TerminalReached && sopSnapshot is not null)
                    {
                        await FinalizeProductAsync(
                            sopRunId,
                            sopResult.CycleId,
                            sopSnapshot,
                            decision,
                            inspectionRecordId: null,
                            ct);
                    }
                    if (_publisher is not null)
                    {
                        await _publisher.PublishAsync(finalEnvelope, ct);
                    }
                });
            }

            var args = new RecordCompletedEventArgs
            {
                StationCode = _recipe.StationCode,
                Record = record,
                Frame = frame,
                Output = output,
                Decision = decision,
                Sop = sopSnapshot,
                CountAfter = Counting.State.CurrentTotal,
            };
            RecordCompleted?.Invoke(this, args);
            return args;
        }
        finally
        {
            _tempStore.Delete(tempPath);
        }
    }

    private ModelSession GetActiveModel()
    {
        if (_sopAlgorithms.Count > 0 && _sopStateMachine?.CurrentStepId is { } stepId
            && _sopAlgorithms.TryGetValue(stepId, out var session))
        {
            return new ModelSession(FindStepRecipe(_recipe, stepId), session);
        }
        return new ModelSession(_recipe, _algorithm!);
    }

    private static Recipe FindStepRecipe(Recipe root, string stepId)
    {
        var step = root.Sop?.Definition?.Steps.FirstOrDefault(item =>
            string.Equals(item.Id, stepId, StringComparison.Ordinal));
        var execution = step?.Execution;
        if (execution is null)
        {
            return root;
        }
        return root with
        {
            PluginId = execution.PluginId,
            TaskType = execution.TaskType,
            ExecutionProvider = execution.ExecutionProvider,
            SettingsJson = execution.SettingsJson,
            Roi = execution.Roi,
            RoiPolicy = execution.RoiPolicy,
            Rules = execution.Rules,
        };
    }

    private (SopSnapshot? Snapshot, long? RunId, string? CycleId, bool TerminalReached) ApplySopEvents(AlgorithmOutput output)
    {
        SopTransition transition;
        long? runId;
        string? cycleId;
        bool terminalReached;
        lock (_sopGate)
        {
            if (_sopStateMachine is null)
            {
                return (null, null, null, false);
            }
            runId = _sopRunId;
            cycleId = _sopCycleId;
            var inputs = BuildSopInputs(output, cycleId);
            transition = _sopStateMachine.ApplyFrame(inputs, output.Sequence, output.Timestamp);
            terminalReached = transition.IsTerminal;
        }

        if (transition.Changed)
        {
            SopChanged?.Invoke(transition.Snapshot);
        }
        return (transition.Snapshot, runId, cycleId, terminalReached);
    }

    private void QueueProductFinalization(
        long? runId,
        string? cycleId,
        SopSnapshot snapshot,
        DecisionResult? decision)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
        {
            return;
        }
        EnqueueWrite(async ct =>
        {
            await FinalizeProductAsync(runId, cycleId, snapshot, decision, null, ct);
        });
    }

    /// <summary>
    /// 统一产品终态路径。内存集合只抑制当前进程内的并发调用，成功或失败都会清理；
    /// 是否已经最终发布由 SopRuns 的持久化状态决定，发布失败可再次进入此方法重试。
    /// </summary>
    private async Task FinalizeProductAsync(
        long? runId,
        string? cycleId,
        SopSnapshot snapshot,
        DecisionResult? decision,
        long? inspectionRecordId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
        {
            return;
        }

        var key = $"{runId?.ToString() ?? cycleId}:final";
        lock (_sopGate)
        {
            if (!_activeSopFinalizations.Add(key))
            {
                return;
            }
        }

        try
        {
            var finalDecision = decision ?? CombineDecision(
                new DecisionResult { Status = DecisionStatus.Processing },
                snapshot);
            var envelope = new ProductResultEnvelope
            {
                ResultId = $"sop-product:{runId?.ToString() ?? cycleId}:final",
                ProjectId = _projectId,
                StationCode = _recipe.StationCode,
                TaskId = _taskId,
                BatchId = BatchId,
                SopRunId = runId,
                CycleId = cycleId,
                Timestamp = DateTimeOffset.UtcNow,
                Decision = finalDecision,
                WorkflowResultJson = JsonSerializer.Serialize(snapshot, JsonOpts),
            };

            Exception? lastError = null;
            var publishClaimId = _sopRuns is not null && runId is not null && _shouldPersist()
                ? $"service:{Environment.ProcessId}:{Guid.NewGuid():N}"
                : null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var shouldPublish = _publisher is not null;
                    if (_sopRuns is not null && runId is { } persistedRunId && _shouldPersist())
                    {
                        shouldPublish = await _sopRuns.PrepareFinalizationAsync(
                            persistedRunId,
                            snapshot,
                            ToStatusString(finalDecision.Status),
                            JsonSerializer.Serialize(finalDecision, JsonOpts),
                            JsonSerializer.Serialize(envelope, JsonOpts),
                            inspectionRecordId,
                            publishClaimId,
                            ct);
                    }

                    if (shouldPublish && _publisher is not null)
                    {
                        await _publisher.PublishProductAsync(envelope, ct);
                        if (_sopRuns is not null && runId is { } publishedRunId && _shouldPersist())
                        {
                            await _sopRuns.MarkFinalPublishedAsync(publishedRunId, publishClaimId, ct);
                        }
                    }
                    return;
                }
                catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested)
                {
                    lastError = ex;
                    if (_sopRuns is not null && runId is { } failedRunId && publishClaimId is not null)
                    {
                        await _sopRuns.ReleaseFinalizationClaimAsync(failedRunId, publishClaimId, ct);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }

            if (lastError is not null)
            {
                if (_sopRuns is not null && runId is { } failedRunId && publishClaimId is not null)
                {
                    await _sopRuns.ReleaseFinalizationClaimAsync(failedRunId, publishClaimId, CancellationToken.None);
                }
                _logger?.LogError(lastError, "产品最终结果持久化/发布失败，将保留可重试状态: cycle={CycleId}", cycleId);
                throw lastError;
            }
        }
        finally
        {
            lock (_sopGate)
            {
                _activeSopFinalizations.Remove(key);
            }
        }
    }

    /// <summary>将算法输出（含 Python 后处理追加的事件）转换为 SOP 标准输入。</summary>
    public static IReadOnlyList<SopInputEvent> BuildSopInputs(AlgorithmOutput output, string? sopCycleId)
    {
        ArgumentNullException.ThrowIfNull(output);
        var inputs = new List<SopInputEvent>(output.Detections.Count + output.Events.Count);
        foreach (var (detection, index) in output.Detections.Select((item, index) => (item, index)))
        {
            inputs.Add(new SopInputEvent
            {
                EventId = $"{output.OutputId}:object:{index}",
                EventType = "object.present",
                SopRunId = sopCycleId,
                Source = "algorithm.detection",
                FrameSequence = output.Sequence,
                OccurredAt = output.Timestamp,
                ClassId = detection.ClassId,
                Confidence = detection.Confidence,
                Box = detection.Box,
            });
        }

        foreach (var visionEvent in output.Events)
        {
            inputs.Add(new SopInputEvent
            {
                EventId = $"{output.OutputId}:vision:{visionEvent.EventId}",
                EventType = visionEvent.EventType,
                SopRunId = sopCycleId,
                Source = "algorithm.event",
                FrameSequence = output.Sequence,
                OccurredAt = visionEvent.StartedAt ?? output.Timestamp,
                Phase = visionEvent.Phase,
                SubjectId = visionEvent.SubjectId,
                Confidence = visionEvent.Confidence,
                RegionId = visionEvent.RegionId,
                TextValue = visionEvent.TextValue,
                CodeValue = visionEvent.CodeValue,
                Count = visionEvent.Count,
                Box = visionEvent.Box,
            });
        }
        return inputs;
    }

    // ---- 内部：后台落库 ----

    private void EnqueueWrite(Func<CancellationToken, Task> write)
    {
        Interlocked.Increment(ref _pendingDbWrites);
        if (!_dbWrites.Writer.TryWrite(write))
        {
            Interlocked.Decrement(ref _pendingDbWrites);
            _logger?.LogWarning("落库队列已关闭，丢弃一次写入");
        }
    }

    private async Task DbWriteLoopAsync()
    {
        // 生命周期跟随通道而非检测令牌：Stop 后在途帧仍会入队写入，循环必须继续消费；
        // 退出仅由 DisposeAsync 的 TryComplete 触发，退出前排空全部积压（DAT-001）。
        while (true)
        {
            Func<CancellationToken, Task>? write = null;
            if (!_dbWrites.Reader.TryRead(out write)
                && !await _dbWrites.Reader.WaitToReadAsync())
            {
                break; // 通道已完成且无积压
            }
            if (write is null)
            {
                continue;
            }
            try
            {
                await write(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "后台落库失败");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingDbWrites);
            }
        }
        // 关闭排空剩余积压
        while (_dbWrites.Reader.TryRead(out var pending))
        {
            try
            {
                await pending(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "关闭排空落库失败");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingDbWrites);
            }
        }
    }

    /// <summary>等待落库队列清空（停止/查询前保证数据可见，最多等 timeout）。</summary>
    private async Task WaitDbWritesAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _pendingDbWrites) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        if (Interlocked.Read(ref _pendingDbWrites) > 0)
        {
            _logger?.LogWarning("等待落库超时，剩余 pending={Pending}", Interlocked.Read(ref _pendingDbWrites));
        }
    }

    private void SetState(DetectionRunState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static bool IsSopTerminal(SopRunStatus status)
        => status is SopRunStatus.CompletedOk
            or SopRunStatus.NgTimeout
            or SopRunStatus.NgConditionFailed
            or SopRunStatus.NgWrongOrder
            or SopRunStatus.Interrupted
            or SopRunStatus.Aborted;

    /// <summary>
    /// 统一产品最终判定：普通视觉规则和 SOP 必须共同通过才允许 OK。
    /// SOP 的错序、超时和条件失败优先覆盖普通规则的 OK；中止/中断不伪造 NG。
    /// </summary>
    public static DecisionResult CombineDecision(DecisionResult visual, SopSnapshot? sop)
    {
        ArgumentNullException.ThrowIfNull(visual);
        if (sop is null)
        {
            return visual;
        }

        var sopMessage = string.IsNullOrWhiteSpace(sop.FailureReason)
            ? $"SOP 未完成（{sop.CompletedCount}/{sop.TotalCount}）"
            : $"SOP：{sop.FailureReason}";
        var sopOutcome = new RuleOutcome
        {
            RuleId = "sop-workflow",
            RuleKind = nameof(SopStateMachine),
            Passed = sop.Status == SopRunStatus.CompletedOk,
            Message = sop.Status == SopRunStatus.CompletedOk ? "SOP 工序全部完成" : sopMessage,
        };
        var outcomes = visual.Outcomes.Append(sopOutcome).ToArray();

        return sop.Status switch
        {
            SopRunStatus.CompletedOk when visual.Status == DecisionStatus.Ok
                => visual with { Outcomes = outcomes },
            SopRunStatus.CompletedOk when visual.Status == DecisionStatus.Unknown
                => new DecisionResult { Status = DecisionStatus.Ok, Outcomes = outcomes },
            SopRunStatus.CompletedOk
                => visual with { Outcomes = outcomes },
            SopRunStatus.NgTimeout or SopRunStatus.NgConditionFailed or SopRunStatus.NgWrongOrder
                => new DecisionResult { Status = DecisionStatus.Ng, Outcomes = outcomes },
            SopRunStatus.ReviewRequired
                => new DecisionResult { Status = DecisionStatus.ReviewRequired, Outcomes = outcomes },
            SopRunStatus.Interrupted or SopRunStatus.Aborted
                => visual.Status is DecisionStatus.Ng or DecisionStatus.ReviewRequired
                    ? visual with { Outcomes = outcomes }
                    : new DecisionResult { Status = DecisionStatus.Processing, Outcomes = outcomes },
            _ => visual.Status switch
            {
                DecisionStatus.Ng => visual with { Outcomes = outcomes },
                DecisionStatus.ReviewRequired => visual with { Outcomes = outcomes },
                _ => new DecisionResult { Status = DecisionStatus.Processing, Outcomes = outcomes },
            },
        };
    }

    private static string ToStatusString(DecisionStatus status) => status switch
    {
        DecisionStatus.Ok => "ok",
        DecisionStatus.Ng => "ng",
        DecisionStatus.ReviewRequired => "review_required",
        DecisionStatus.Processing => "processing",
        DecisionStatus.Unknown => "unknown",
        DecisionStatus.Error => "error",
        _ => "error",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 流水计数参数合并进插件 settings（配方单一来源）：
    /// countingMode（"unique"/"line"）与 line{ax,ay,bx,by,hysteresis}，
    /// 键名与 sample-flow-counter 的 settings.schema.json 对齐；快照模式原样透传。
    /// </summary>
    public static JsonElement? BuildAlgorithmSettings(Recipe recipe)
    {
        var isYolo11 = recipe.PluginId.Contains("yolo11", StringComparison.OrdinalIgnoreCase);
        JsonObject? obj = null;
        if (!string.IsNullOrWhiteSpace(recipe.SettingsJson))
        {
            try
            {
                obj = JsonNode.Parse(recipe.SettingsJson) as JsonObject;
            }
            catch (JsonException)
            {
                // 设置损坏 → 只带计数参数
            }
        }
        if (recipe.CountingMode == CountingMode.Snapshot && !isYolo11)
            return TryParseJson(recipe.SettingsJson);
        obj ??= [];
        if (recipe.CountingMode != CountingMode.Snapshot)
        {
            obj["countingMode"] = recipe.CountingMode == CountingMode.UniqueTracking ? "unique" : "line";
            var line = recipe.CountingLine ?? new CountingLineConfig
            {
                A = new NormalizedPoint { X = 0.5, Y = 0.1 },
                B = new NormalizedPoint { X = 0.5, Y = 0.9 },
            };
            obj["line"] = new JsonObject
            {
                ["ax"] = line.A.X,
                ["ay"] = line.A.Y,
                ["bx"] = line.B.X,
                ["by"] = line.B.Y,
                ["hysteresis"] = line.Hysteresis,
            };
        }
        if (isYolo11)
        {
            // YOLO11 使用 settings.device 选择 PyTorch 推理设备。
            obj["device"] = recipe.ExecutionProvider;
            if (recipe.TaskType == InspectionTaskType.BehaviorRecognition)
            {
                // 行为识别必须由 YOLO11-Pose 提取人体关键点。旧版本可能把
                // 行为训练产生的 TorchScript best.pt 存进了主模型字段，
                // 这里在运行前移除该错误路径，让插件回退到 yolo11n-pose.pt。
                obj["task"] = "pose";
                if (obj["modelPath"]?.GetValue<string>() is { } modelPath
                    && modelPath.Contains("behavior-models", StringComparison.OrdinalIgnoreCase))
                {
                    obj.Remove("modelPath");
                }
            }
            // 配置了行为区域时自动启用 Ultralytics ByteTrack；普通检测任务保持原模式。
            obj["tracking"] = recipe.Behavior.Enabled
                && (recipe.Behavior.Zones.Count > 0 || recipe.Behavior.Fall.Enabled);
        }
        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonElement? TryParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (State is DetectionRunState.Running or DetectionRunState.Paused)
        {
            try
            {
                await StopAsync();
            }
            catch (Exception)
            {
                // 退出清理
            }
        }
        if (_camera is not null)
        {
            DetachCamera(_camera);
        }
        if (_algorithm is not null)
        {
            _algorithm.Faulted -= OnAlgorithmFaulted;
        }
        _cts.Cancel();
        _dbWrites.Writer.TryComplete();
        if (_dbLoopTask is not null)
        {
            try
            {
                await _dbLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 尽力而为
            }
        }
        _cts.Dispose();
    }
}
