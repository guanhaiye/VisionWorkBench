namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>暂停采集线程但保留会话，供开始按钮恢复。</summary>
public sealed class AsyncPauseGate : IDisposable
{
    private readonly SemaphoreSlim _resumeSignal = new(0, 1);
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public void Pause() => Interlocked.Exchange(ref _paused, 1);

    public void Resume()
    {
        if (Interlocked.Exchange(ref _paused, 0) == 1)
        {
            _resumeSignal.Release();
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            await _resumeSignal.WaitAsync(cancellationToken);
        }
    }

    public void Dispose() => _resumeSignal.Dispose();
}
