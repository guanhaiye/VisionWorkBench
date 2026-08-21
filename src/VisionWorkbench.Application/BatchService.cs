using VisionWorkbench.Domain;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

/// <summary>批次服务（CNT-L-012 基础）：开始/结束批次，维护当前批次计数快照。</summary>
public sealed class BatchService(BatchRepository batches)
{
    private long? _currentBatchId;

    public long? CurrentBatchId => _currentBatchId;

    public async Task<BatchEntity> StartAsync(long taskId, long initialCounterValue,
        string projectId = "default", string stationCode = "", CancellationToken ct = default)
    {
        var batch = await batches.StartAsync(taskId, initialCounterValue, projectId, stationCode, ct);
        _currentBatchId = batch.Id;
        return batch;
    }

    /// <summary>
    /// 进程重启后恢复仍处于 running 的批次；没有未结束批次时才创建新批次（CNT-L-014/STB-005）。
    /// </summary>
    public async Task<BatchEntity> ResumeOrStartAsync(
        long taskId,
        CountingService counting,
        RecordRepository records,
        string projectId = "default",
        string stationCode = "",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(counting);
        ArgumentNullException.ThrowIfNull(records);
        var running = await batches.FindRunningAsync(
            taskId,
            string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            string.IsNullOrWhiteSpace(stationCode) ? null : stationCode,
            ct);
        if (running is null)
        {
            return await StartAsync(taskId, counting.State.CurrentTotal, projectId, stationCode, ct);
        }

        var events = await records.ListCountingEventsAsync(running.Id, ct);
        counting.RestoreFrom(events.Select(ToDomainEvent));
        _currentBatchId = running.Id;
        return running;
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

    private static CountingEvent ToDomainEvent(CountingEventEntity entity) => new()
    {
        EventId = $"db-{entity.Id}",
        CounterId = entity.CounterId,
        TrackId = entity.TrackId,
        ClassId = entity.ClassId,
        Type = Enum.TryParse<CountingEventType>(entity.EventType, out var type)
            ? type
            : CountingEventType.Appeared,
        Direction = entity.Direction,
        Delta = entity.Delta,
        Confidence = entity.Confidence,
        OccurredAt = new DateTimeOffset(
            DateTime.SpecifyKind(entity.OccurredAt, DateTimeKind.Utc), TimeSpan.Zero),
        FrameSequence = entity.FrameSequence,
    };
}
