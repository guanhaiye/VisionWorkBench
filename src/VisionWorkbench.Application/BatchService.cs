using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

/// <summary>批次服务（CNT-L-012 基础）：开始/结束批次，维护当前批次计数快照。</summary>
public sealed class BatchService(BatchRepository batches)
{
    private long? _currentBatchId;

    public long? CurrentBatchId => _currentBatchId;

    public async Task<BatchEntity> StartAsync(long taskId, long initialCounterValue, CancellationToken ct = default)
    {
        var batch = await batches.StartAsync(taskId, initialCounterValue, ct);
        _currentBatchId = batch.Id;
        return batch;
    }

    public async Task<BatchEntity?> EndAsync(long finalCounterValue, string status = "completed",
        CancellationToken ct = default)
    {
        if (_currentBatchId is not { } id)
        {
            return null;
        }
        var batch = await batches.EndAsync(id, finalCounterValue, status, ct);
        _currentBatchId = null;
        return batch;
    }
}
