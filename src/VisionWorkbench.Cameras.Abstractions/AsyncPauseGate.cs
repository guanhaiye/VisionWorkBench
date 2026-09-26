namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>暂停采集线程但保留会话，供开始按钮恢复。</summary>
public sealed class AsyncPauseGate : IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resumeSignal;
    private bool _disposed;

    public bool IsPaused
    {
        get { lock (_gate) return _resumeSignal is not null; }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _resumeSignal ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _resumeSignal?.TrySetResult();
            _resumeSignal = null;
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? resume;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                resume = _resumeSignal?.Task;
            }
            if (resume is null) return;
            await resume.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _resumeSignal?.TrySetException(new ObjectDisposedException(nameof(AsyncPauseGate)));
            _resumeSignal = null;
        }
    }
}
