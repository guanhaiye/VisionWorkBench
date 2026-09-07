using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public sealed record RecoveryAttempt(string Component, bool Succeeded, int Attempts, string Message, DateTime CompletedAtUtc);

/// <summary>统一的有限重试自恢复编排器：恢复失败会返回明确结果，由健康服务产生告警。</summary>
public sealed class HealthRecoveryService(HealthService health)
{
    public async Task<RecoveryAttempt> RecoverAsync(string component, Func<CancellationToken, Task> action, int attempts = 3, TimeSpan? delay = null, CancellationToken ct = default)
    {
        var wait = delay ?? TimeSpan.FromSeconds(2);
        Exception? last = null;
        for (var index = 1; index <= Math.Clamp(attempts, 1, 5); index++)
        {
            try { await action(ct); return new RecoveryAttempt(component, true, index, "恢复成功", DateTime.UtcNow); }
            catch (Exception ex) when (index < attempts && !ct.IsCancellationRequested) { last = ex; await Task.Delay(wait, ct); }
            catch (Exception ex) { last = ex; break; }
        }
        await health.RaiseOrRefreshAlertAsync("RECOVERY-" + component.ToUpperInvariant(), "critical", component + " 自动恢复失败", last?.Message ?? "未知错误", ct);
        return new RecoveryAttempt(component, false, Math.Clamp(attempts, 1, 5), last?.Message ?? "恢复失败", DateTime.UtcNow);
    }
}
