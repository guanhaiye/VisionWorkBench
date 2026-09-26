using System.Threading.Channels;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Application;

/// <summary>帧路由策略（文档 FRM-001/002/003）。</summary>
public enum FrameRoutingStrategy
{
    /// <summary>只保留最新帧：推理慢时旧帧直接丢弃（实时检测默认）。</summary>
    LatestOnly,

    /// <summary>有界队列：最多积压 N 帧，满了丢最老（按序处理场景）。</summary>
    Bounded,
}

/// <summary>
/// 帧调度器：相机帧 → 预览 tap + 推理队列。
/// 预览每帧都发（UI 自行编组降频）；推理侧按策略取帧，慢消费时丢帧并计数（FRM-002）。
/// </summary>
public sealed class FrameScheduler
{
    private readonly object _latestLock = new();
    private readonly SemaphoreSlim _latestSignal = new(0, 1);
    private readonly Channel<VideoFrame>? _queue;
    private VideoFrame? _latest;
    private long _dropped;
    private long _seen;
    private bool _completed;

    public FrameRoutingStrategy Strategy { get; }
    public long SeenFrameCount => Interlocked.Read(ref _seen);
    public long DroppedFrameCount => Interlocked.Read(ref _dropped);

    /// <summary>预览 tap：相机线程直接回调，UI 必须自行 Dispatcher 编组。</summary>
    public event EventHandler<VideoFrame>? PreviewReceived;

    public event EventHandler? SourceCompleted;

    public FrameScheduler(FrameRoutingStrategy strategy = FrameRoutingStrategy.LatestOnly, int boundedCapacity = 4)
    {
        Strategy = strategy;
        if (strategy == FrameRoutingStrategy.Bounded)
        {
            // Bounded is a legacy name; callers rely on lossless, ordered delivery
            // for finite file sources. DropOldest breaks tracking under load.
            ArgumentOutOfRangeException.ThrowIfLessThan(boundedCapacity, 1);
            _queue = Channel.CreateUnbounded<VideoFrame>(new UnboundedChannelOptions
            {
                // ClearPendingFrames can drain while inference is reading.
                SingleReader = false,
                SingleWriter = false,
            });
        }
    }

    /// <summary>相机帧入口（相机回调线程调用）。</summary>
    public void OnFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Interlocked.Increment(ref _seen);
        PreviewReceived?.Invoke(this, frame);

        if (_queue is not null)
        {
            if (!_queue.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref _dropped);
            }
            return;
        }

        lock (_latestLock)
        {
            if (_completed)
            {
                return;
            }
            if (_latest is not null)
            {
                // 上一帧还没被消费，覆盖即丢弃（FRM-002）
                Interlocked.Increment(ref _dropped);
            }
            _latest = frame;
            SignalLatest();
        }
    }

    /// <summary>有限源（图片目录/视频）播完时调用；消费者最终会收到 null。</summary>
    public void Complete()
    {
        lock (_latestLock)
        {
            if (_completed) return;
            _completed = true;
            _queue?.Writer.TryComplete();
            SignalLatest();
        }
        SourceCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>暂停时丢弃已经排队但尚未开始处理的帧，恢复后只处理新帧。</summary>
    public void ClearPendingFrames()
    {
        if (_queue is not null)
        {
            while (_queue.Reader.TryRead(out _))
            {
            }
            return;
        }

        lock (_latestLock)
        {
            _latest = null;
            // Drain atomically with publishing so a new frame cannot lose its wakeup.
            _latestSignal.Wait(0);
            if (_completed) SignalLatest();
        }
    }

    // Producers hold _latestLock. The latest-frame slot needs only one wakeup.
    private void SignalLatest()
    {
        if (_latestSignal.CurrentCount == 0) _latestSignal.Release();
    }

    /// <summary>
    /// 取下一帧待推理；源已播完且无剩余帧时返回 null。
    /// LatestOnly：无新帧时异步等待（相机流）或等 Complete（有限源）。
    /// </summary>
    public async Task<VideoFrame?> TakeNextAsync(CancellationToken cancellationToken)
    {
        if (_queue is not null)
        {
            while (true)
            {
                if (_queue.Reader.TryRead(out var queued))
                {
                    return queued;
                }
                if (_queue.Reader.Completion.IsCompleted)
                {
                    return null;
                }
                try
                {
                    return await _queue.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_latestLock)
            {
                if (_latest is { } frame)
                {
                    _latest = null;
                    return frame;
                }
                // Completion is persistent state; every later read must also finish.
                if (_completed) return null;
            }
            await _latestSignal.WaitAsync(cancellationToken);
        }
    }
}
