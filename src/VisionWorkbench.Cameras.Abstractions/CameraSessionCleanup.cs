namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>在采集线程结束后释放原生资源；关闭等待超时不能缩短原生资源的生命周期。</summary>
public static class CameraSessionCleanup
{
    /// <returns>资源是否已在指定等待时间内释放；false 表示正在等待采集线程结束后清理。</returns>
    public static async Task<bool> ReleaseAfterLoopAsync(
        Task? loopTask, Action releaseResources, TimeSpan waitTimeout, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(releaseResources);
        var cleanup = Task.Run(async () =>
        {
            try
            {
                if (loopTask is not null) await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Report(ex); }
            finally
            {
                try { releaseResources(); }
                catch (Exception ex) { Report(ex); }
            }
        });
        try
        {
            await cleanup.WaitAsync(waitTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // cleanup 持有资源引用且会观察异常，原生读取返回后自动释放。
            return false;
        }

        void Report(Exception error)
        {
            try { onError?.Invoke(error); }
            catch { /* 错误报告本身不能中断资源清理。 */ }
        }
    }
}
