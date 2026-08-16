using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

/// <summary>分页结果。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int PageIndex, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(Total / (double)Math.Max(1, PageSize));
}

/// <summary>历史记录查询条件（历史页筛选，文档 §8）。</summary>
public sealed record RecordQuery(
    long? TaskId = null,
    long? BatchId = null,
    string? Status = null,
    DateTime? From = null,
    DateTime? To = null,
    string? Keyword = null);

/// <summary>任务（配方）仓储（CFG-004）。每次操作短生命周期 Context（线程安全）。</summary>
public sealed class TaskRepository(IDbContextFactory<VisionDbContext> factory)
{
    public async Task<List<TaskEntity>> ListAsync(CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.Tasks.AsNoTracking().OrderByDescending(t => t.UpdatedAt).ToListAsync(ct);
    }

    public async Task<TaskEntity?> FindAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<TaskEntity> SaveAsync(TaskEntity entity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entity.Name))
        {
            throw new ArgumentException("任务名不能为空（CFG-004）");
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await using var db = factory.CreateDbContext();
        if (entity.Id == 0)
        {
            entity.CreatedAt = DateTime.UtcNow;
            db.Tasks.Add(entity);
        }
        else
        {
            var existing = await db.Tasks.FirstOrDefaultAsync(t => t.Id == entity.Id, ct)
                ?? throw new InvalidOperationException($"任务不存在: {entity.Id}");
            db.Entry(existing).CurrentValues.SetValues(entity);
        }
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}

/// <summary>批次仓储（CNT-L-012）。</summary>
public sealed class BatchRepository(IDbContextFactory<VisionDbContext> factory)
{
    public async Task<BatchEntity> StartAsync(long taskId, long initialCounterValue, CancellationToken ct = default)
    {
        var batch = new BatchEntity
        {
            TaskId = taskId,
            BatchNumber = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
            Status = "running",
            InitialCounterValue = initialCounterValue,
        };
        await using var db = factory.CreateDbContext();
        db.Batches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<BatchEntity?> EndAsync(long batchId, long finalCounterValue, string status = "completed",
        CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null)
        {
            return null;
        }
        batch.EndedAt = DateTime.UtcNow;
        batch.FinalCounterValue = finalCounterValue;
        batch.Status = status;
        await db.SaveChangesAsync(ct);
        return batch;
    }
}

/// <summary>检测记录 + 事件 + 纠错仓储。</summary>
public sealed class RecordRepository(IDbContextFactory<VisionDbContext> factory)
{
    public async Task<InspectionRecordEntity> AddAsync(
        InspectionRecordEntity record,
        IReadOnlyList<Contracts.Results.CountingEvent>? countingEvents = null,
        IReadOnlyList<Contracts.Results.VisionEvent>? visionEvents = null,
        CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        db.InspectionRecords.Add(record);
        if (countingEvents is { Count: > 0 })
        {
            foreach (var e in countingEvents)
            {
                db.CountingEvents.Add(new CountingEventEntity
                {
                    Record = record,
                    BatchId = record.BatchId,
                    CounterId = e.CounterId,
                    TrackId = e.TrackId,
                    ClassId = e.ClassId,
                    EventType = e.Type.ToString(),
                    Direction = e.Direction,
                    Delta = e.Delta,
                    Confidence = e.Confidence,
                    OccurredAt = e.OccurredAt.UtcDateTime,
                    FrameSequence = e.FrameSequence,
                    EvidenceImagePath = e.EvidenceImagePath,
                });
            }
        }
        if (visionEvents is { Count: > 0 })
        {
            foreach (var e in visionEvents)
            {
                db.VisionEvents.Add(new VisionEventEntity
                {
                    Record = record,
                    BatchId = record.BatchId,
                    EventType = e.EventType,
                    Phase = e.Phase.ToString(),
                    Severity = e.Severity.ToString().ToLowerInvariant(),
                    SubjectId = e.SubjectId,
                    RegionId = e.RegionId,
                    StartedAt = e.StartedAt?.UtcDateTime,
                    EndedAt = e.EndedAt?.UtcDateTime,
                    Confidence = e.Confidence,
                    EvidenceImagePath = e.EvidenceImagePath,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>分页查询（历史页，DAT-001/003）。</summary>
    public async Task<PagedResult<InspectionRecordEntity>> QueryAsync(
        RecordQuery query, int pageIndex = 0, int pageSize = 50, CancellationToken ct = default)
    {
        pageIndex = Math.Max(0, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 500);
        await using var db = factory.CreateDbContext();
        var q = db.InspectionRecords.AsNoTracking();
        if (query.TaskId is { } taskId)
        {
            q = q.Where(r => r.TaskId == taskId);
        }
        if (query.BatchId is { } batchId)
        {
            q = q.Where(r => r.BatchId == batchId);
        }
        if (!string.IsNullOrEmpty(query.Status))
        {
            q = q.Where(r => r.Status == query.Status);
        }
        if (query.From is { } from)
        {
            q = q.Where(r => r.StartedAt >= from);
        }
        if (query.To is { } to)
        {
            q = q.Where(r => r.StartedAt <= to);
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.StartedAt)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<InspectionRecordEntity>(items, total, pageIndex, pageSize);
    }

    public async Task<InspectionRecordEntity?> FindAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.InspectionRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>人工纠错：写 Corrections + 标记 WasCorrected（CNT-S-010/DAT）。</summary>
    public async Task<CorrectionEntity> AddCorrectionAsync(
        long recordId, string beforeJson, string afterJson, string reason, string operatorName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("纠错必须填写原因（CNT-S-010）");
        }
        await using var db = factory.CreateDbContext();
        var record = await db.InspectionRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct)
            ?? throw new InvalidOperationException($"检测记录不存在: {recordId}");
        var correction = new CorrectionEntity
        {
            RecordId = recordId,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            OperatorName = operatorName,
        };
        db.Corrections.Add(correction);
        record.WasCorrected = true;
        await db.SaveChangesAsync(ct);
        return correction;
    }

    public async Task<List<CorrectionEntity>> ListCorrectionsAsync(
        long recordId, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.Corrections.AsNoTracking()
            .Where(c => c.RecordId == recordId)
            .OrderBy(c => c.CorrectedAt)
            .ToListAsync(ct);
    }

    public async Task<List<CountingEventEntity>> ListCountingEventsAsync(
        long? batchId = null, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.CountingEvents.AsNoTracking()
            .Where(e => batchId == null || e.BatchId == batchId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);
    }

    /// <summary>直接追加一条计数事件（人工修正 Corrected/CounterReset 落库）。</summary>
    public async Task AppendCountingEventAsync(
        Contracts.Results.CountingEvent evt, long? batchId, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        db.CountingEvents.Add(new CountingEventEntity
        {
            BatchId = batchId,
            CounterId = evt.CounterId,
            TrackId = evt.TrackId,
            ClassId = evt.ClassId,
            EventType = evt.Type.ToString(),
            Direction = evt.Direction,
            Delta = evt.Delta,
            Confidence = evt.Confidence,
            OccurredAt = evt.OccurredAt.UtcDateTime,
            FrameSequence = evt.FrameSequence,
            EvidenceImagePath = evt.EvidenceImagePath,
        });
        await db.SaveChangesAsync(ct);
    }
}
