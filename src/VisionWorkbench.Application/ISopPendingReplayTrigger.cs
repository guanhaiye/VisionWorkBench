namespace VisionWorkbench.Application;

/// <summary>快速发出持久化 SOP 产品结果重放唤醒信号；实现必须支持并发调用。</summary>
public interface ISopPendingReplayTrigger
{
    Task TriggerAsync(CancellationToken cancellationToken = default);
}
