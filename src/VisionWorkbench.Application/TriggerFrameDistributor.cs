using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Application;

/// <summary>
/// 触发帧分配器：只保留一个最新帧，并把满足触发时刻的帧广播给当时的等待者。
/// 不保存历史帧，因此空闲相机不会因无人请求而持续增长内存。
/// </summary>
public sealed class TriggerFrameDistributor
{
    private sealed class Waiter
    {
        public required DateTimeOffset TriggeredAt { get; init; }
        public required TaskCompletionSource<VideoFrame> Completion { get; init; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private VideoFrame? _latest;
    private bool _completed;

    public int WaitingCount
    {
        get { lock (_gate) return _waiters.Count; }
    }

    public bool HasBufferedFrame
    {
        get { lock (_gate) return _latest is not null; }
    }

    public Task<VideoFrame> WaitForFrameAsync(DateTimeOffset triggeredAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_latest is { } latest && latest.Timestamp >= triggeredAt)
            {
                _latest = null;
                return Task.FromResult(latest);
            }
            if (_completed)
                return Task.FromException<VideoFrame>(new InvalidOperationException("帧分配器已停止"));

            var waiter = new Waiter
            {
                TriggeredAt = triggeredAt,
                Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _waiters.Add(waiter);
            if (cancellationToken.CanBeCanceled)
                waiter.CancellationRegistration = cancellationToken.Register(() => Cancel(waiter));
            return waiter.Completion.Task;
        }
    }

    public void Publish(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        List<Waiter>? ready = null;
        lock (_gate)
        {
            if (_completed) return;
            _latest = frame;
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (frame.Timestamp < waiter.TriggeredAt) continue;
                ready ??= [];
                ready.Add(waiter);
                _waiters.RemoveAt(index);
            }
        }

        if (ready is null) return;
        foreach (var waiter in ready)
        {
            waiter.CancellationRegistration.Dispose();
            waiter.Completion.TrySetResult(frame);
        }
    }

    public void Complete(Exception? error = null)
    {
        Waiter[] waiters;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _latest = null;
            waiters = [.. _waiters];
            _waiters.Clear();
        }
        foreach (var waiter in waiters)
        {
            waiter.CancellationRegistration.Dispose();
            if (error is null) waiter.Completion.TrySetCanceled();
            else waiter.Completion.TrySetException(error);
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate) _waiters.Remove(waiter);
        waiter.Completion.TrySetCanceled();
    }
}
