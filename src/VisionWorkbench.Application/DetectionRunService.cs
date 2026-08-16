using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Cameras.Abstractions;
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
    public required InspectionRecordEntity Record { get; init; }
    public required AlgorithmOutput Output { get; init; }
    public required DecisionResult Decision { get; init; }
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
    private readonly Channel<Func<CancellationToken, Task>> _dbWrites =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly string _counterId;
    private Task? _dbLoopTask;
    private long _pendingDbWrites;
    private Task? _processingLoopTask;

    public DetectionRunState State { get; private set; } = DetectionRunState.Idle;
    public FrameScheduler Scheduler { get; private set; } = new();
    public CountingService Counting { get; }
    public long? BatchId { get; private set; }

    private ICameraSession? _camera;
    private IAlgorithmSession? _algorithm;
    private Recipe _recipe = null!;
    private long _taskId;
    private string _evidenceDir = "";
    private volatile bool _paused;

    public event EventHandler<RecordCompletedEventArgs>? RecordCompleted;
    public event EventHandler<RunFaultedEventArgs>? Faulted;
    public event EventHandler? SourceCompleted;
    public event EventHandler<DetectionRunState>? StateChanged;

    /// <summary>预览帧 tap（相机线程回调；UI 订阅方必须自行 Dispatcher 编组）。</summary>
    public event EventHandler<VideoFrame>? PreviewReceived;

    public DetectionRunService(
        RecordRepository records,
        TempImageStore tempStore,
        ILogger<DetectionRunService>? logger = null,
        string? counterId = null)
    {
        _records = records;
        _tempStore = tempStore;
        _logger = logger;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(algorithm);
        if (State is DetectionRunState.Running or DetectionRunState.Paused)
        {
            throw new InvalidOperationException("检测已在运行，先停止再开始");
        }

        _recipe = recipe;
        _taskId = taskId;
        _camera = camera;
        _algorithm = algorithm;
        BatchId = batchId;
        _evidenceDir = Path.Combine(
            Path.GetDirectoryName(_tempStore.RootDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? ".",
            "evidence", DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(_evidenceDir);

        // 算法初始化（settings JSON → JsonElement）+ 启动会话
        await algorithm.InitializeAsync(new AlgorithmInitialization
        {
            Settings = TryParseJson(recipe.SettingsJson),
        }, cancellationToken);
        await algorithm.StartAsync(new AlgorithmStartOptions
        {
            Mode = "stream",
            Roi = recipe.Roi,
        }, cancellationToken);

        // 帧调度器接线
        Scheduler = new FrameScheduler(routingStrategy);
        Scheduler.PreviewReceived += (s, frame) => PreviewReceived?.Invoke(s, frame);
        camera.FrameReceived += OnCameraFrame;
        camera.Completed += OnCameraCompleted;
        camera.Faulted += OnCameraFaulted;
        await camera.StartAsync(cancellationToken);

        _paused = false;
        SetState(DetectionRunState.Running);
        _processingLoopTask = Task.Run(() => ProcessingLoopAsync(_cts.Token));
        _logger?.LogInformation("检测开始: task={TaskId} batch={BatchId} counter={Counter}",
            taskId, batchId, _counterId);
    }

    public Task PauseAsync()
    {
        if (State == DetectionRunState.Running)
        {
            _paused = true;
            SetState(DetectionRunState.Paused);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (State == DetectionRunState.Paused)
        {
            _paused = false;
            SetState(DetectionRunState.Running);
        }
        return Task.CompletedTask;
    }

    /// <summary>单次检测：取最新一帧走完整流水线（有限源时取下一帧）。</summary>
    public async Task<RecordCompletedEventArgs?> SubmitSingleAsync(TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (State is DetectionRunState.Idle or DetectionRunState.Stopped or DetectionRunState.Faulted)
        {
            return null;
        }
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
            return null; // 等帧超时
        }
    }

    /// <summary>停止检测（不结束批次；批次由 BatchService 管理）。</summary>
    public async Task StopAsync()
    {
        if (State is DetectionRunState.Idle or DetectionRunState.Stopped)
        {
            return;
        }
        SetState(DetectionRunState.Stopped);
        _cts.Cancel();

        if (_camera is not null)
        {
            _camera.FrameReceived -= OnCameraFrame;
            _camera.Completed -= OnCameraCompleted;
            _camera.Faulted -= OnCameraFaulted;
            await _camera.StopAsync(CancellationToken.None);
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
        if (_algorithm is not null)
        {
            try
            {
                await _algorithm.StopAsync(CancellationToken.None);
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

    /// <summary>计数清零（CNT-S-010）。</summary>
    public Task<CountingAdjustment> ResetCountAsync(string reason, string @operator)
    {
        var adjustment = Counting.Reset(reason, @operator);
        EnqueueWrite(ct => _records.AppendCountingEventAsync(adjustment.Event, BatchId, ct));
        return Task.FromResult(adjustment);
    }

    // ---- 内部：相机事件 → 调度器 ----

    private void OnCameraFrame(object? sender, VideoFrameReceivedEventArgs e) => Scheduler.OnFrame(e.Frame);

    private void OnCameraCompleted(object? sender, EventArgs e)
    {
        Scheduler.Complete();
        SourceCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnCameraFaulted(object? sender, CameraFaultedEventArgs e)
    {
        SetState(DetectionRunState.Faulted);
        Faulted?.Invoke(this, new RunFaultedEventArgs
        {
            Source = "camera",
            Code = e.Fault.Code,
            Message = e.Fault.Message,
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
                break;
            }
            if (frame is null)
            {
                // 有限源播完
                _logger?.LogInformation("输入源播放完毕，检测循环退出");
                break;
            }
            if (_paused)
            {
                continue; // 暂停时丢弃（预览仍走 PreviewReceived）
            }
            try
            {
                await ProcessFrameAsync(frame);
            }
            catch (AlgorithmFaultException ex)
            {
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
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "处理帧失败，跳过 sequence={Seq}", frame.Sequence);
            }
        }
        if (State == DetectionRunState.Running)
        {
            SetState(DetectionRunState.Idle);
        }
    }

    private async Task<RecordCompletedEventArgs> ProcessFrameAsync(VideoFrame frame)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tempPath = _tempStore.SaveFrame(frame);
        try
        {
            var output = await _algorithm!.SubmitAsync(new AlgorithmInput
            {
                InputId = $"in-{frame.Sequence}",
                ImagePath = tempPath,
                FrameSequence = frame.Sequence,
                CapturedAt = frame.Timestamp,
                Roi = _recipe.Roi,
            }, CancellationToken.None); // 在途帧必须完成（Stop 只阻止取新帧，不打断推理）

            var decision = RuleEngine.Evaluate(output, _recipe.Rules, _recipe.Roi, _recipe.RoiPolicy);
            Counting.ApplyOutput(output);
            sw.Stop();

            // 证据图（DAT-002）：NG/待确认保存原图+标注图，OK 只存结构化数据
            string? origPath = null;
            string? annotPath = null;
            if (decision.Status is DecisionStatus.Ng or DecisionStatus.ReviewRequired)
            {
                var stem = $"{DateTimeOffset.UtcNow:HHmmss}-{frame.Sequence}";
                origPath = Path.Combine(_evidenceDir, $"{stem}-orig.png");
                annotPath = Path.Combine(_evidenceDir, $"{stem}-annot.png");
                System.IO.File.WriteAllBytes(origPath, System.IO.File.ReadAllBytes(tempPath));
                ImageAnnotator.AnnotateToFile(frame, output, decision, annotPath);
            }

            var record = new InspectionRecordEntity
            {
                TaskId = _taskId,
                BatchId = BatchId,
                StartedAt = startedAt.UtcDateTime,
                CompletedAt = DateTime.UtcNow,
                Status = ToStatusString(decision.Status),
                OriginalImagePath = origPath,
                AnnotatedImagePath = annotPath,
                AlgorithmElapsedMs = output.Performance?.TotalMs ?? 0,
                TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
                RawResultJson = JsonSerializer.Serialize(output, JsonOpts),
                FinalResultJson = JsonSerializer.Serialize(decision, JsonOpts),
            };

            EnqueueWrite(ct => _records.AddAsync(record, output.CountingEvents, output.Events, ct));

            var args = new RecordCompletedEventArgs
            {
                Record = record,
                Output = output,
                Decision = decision,
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

    private static string ToStatusString(DecisionStatus status) => status switch
    {
        DecisionStatus.Ok => "ok",
        DecisionStatus.Ng => "ng",
        DecisionStatus.ReviewRequired => "review_required",
        DecisionStatus.Error => "error",
        _ => "error",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
