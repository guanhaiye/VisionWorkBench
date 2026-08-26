using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace VisionWorkbench.Persistence;

/// <summary>分页结果。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int PageIndex, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(Total / (double)Math.Max(1, PageSize));
}

/// <summary>历史记录查询条件（历史页筛选，文档 §8）。</summary>
public sealed record RecordQuery(
    string? ProjectId = null,
    long? TaskId = null,
    string? StationCode = null,
    long? BatchId = null,
    string? Status = null,
    DateTime? From = null,
    DateTime? To = null,
    string? Keyword = null,
    string? ModelVersion = null);

/// <summary>项目与工位仓储（STA-001~014）。</summary>
public sealed class ProjectStationRepository(IDbContextFactory<VisionDbContext> factory)
{
    public async Task<List<ProjectEntity>> ListProjectsAsync(CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.Projects.AsNoTracking().OrderBy(p => p.ProjectCode).ToListAsync(ct);
    }

    public async Task<ProjectEntity> SaveProjectAsync(ProjectEntity project, CancellationToken ct = default)
    {
        var code = project.ProjectCode.Trim();
        if (code.Length is 0 or > 64)
        {
            throw new ArgumentException("项目编号不能为空且不能超过 64 个字符");
        }
        if (string.IsNullOrWhiteSpace(project.Name))
        {
            throw new ArgumentException("项目名称不能为空");
        }
        project.ProjectCode = code;
        project.UpdatedAt = DateTime.UtcNow;
        await using var db = factory.CreateDbContext();
        if (project.Id == 0)
        {
            project.CreatedAt = DateTime.UtcNow;
            db.Projects.Add(project);
        }
        else
        {
            var current = await db.Projects.FirstOrDefaultAsync(p => p.Id == project.Id, ct)
                ?? throw new InvalidOperationException($"项目不存在: {project.Id}");
            current.ProjectCode = project.ProjectCode;
            current.Name = project.Name;
            current.Description = project.Description;
            current.UpdatedAt = project.UpdatedAt;
        }
        await db.SaveChangesAsync(ct);
        return project;
    }

    public async Task<List<StationEntity>> ListStationsAsync(long projectId, bool includeArchived = false,
        CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var query = db.Stations.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (!includeArchived)
        {
            query = query.Where(s => !s.IsArchived);
        }
        return await query.OrderBy(s => s.StationCode).ToListAsync(ct);
    }

    public async Task<StationEntity> SaveStationAsync(StationEntity station, CancellationToken ct = default)
    {
        var code = station.StationCode.Trim();
        if (code.Length is 0 or > 64)
        {
            throw new ArgumentException("工位编号不能为空且不能超过 64 个字符（STA-003）");
        }
        if (string.IsNullOrWhiteSpace(station.Name))
        {
            throw new ArgumentException("工位名称不能为空");
        }
        station.StationCode = code;
        station.UpdatedAt = DateTime.UtcNow;
        await using var db = factory.CreateDbContext();
        if (!await db.Projects.AnyAsync(p => p.Id == station.ProjectId, ct))
        {
            throw new InvalidOperationException("所属项目不存在");
        }
        var duplicate = await db.Stations.AnyAsync(s => s.ProjectId == station.ProjectId
            && s.Id != station.Id && s.StationCode.ToLower() == code.ToLower(), ct);
        if (duplicate)
        {
            throw new InvalidOperationException($"项目内工位编号已存在: {code}（STA-003）");
        }
        if (station.Id == 0)
        {
            station.CreatedAt = DateTime.UtcNow;
            db.Stations.Add(station);
        }
        else
        {
            var current = await db.Stations.FirstOrDefaultAsync(s => s.Id == station.Id, ct)
                ?? throw new InvalidOperationException($"工位不存在: {station.Id}");
            if (current.IsArchived)
            {
                throw new InvalidOperationException("已归档工位不能直接编辑");
            }
            current.ProjectId = station.ProjectId;
            current.StationCode = station.StationCode;
            current.Name = station.Name;
            current.Enabled = station.Enabled;
            current.TaskId = station.TaskId;
            current.CameraProviderId = station.CameraProviderId;
            current.CameraDeviceId = station.CameraDeviceId;
            current.UpdatedAt = station.UpdatedAt;
        }
        await db.SaveChangesAsync(ct);
        return station;
    }

    public async Task<bool> ArchiveOrDeleteStationAsync(long stationId, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == stationId, ct);
        if (station is null)
        {
            return false;
        }
        var projectCode = await db.Projects.Where(p => p.Id == station.ProjectId)
            .Select(p => p.ProjectCode).FirstOrDefaultAsync(ct);
        var hasHistory = projectCode is not null && await db.InspectionRecords.AnyAsync(
            r => r.ProjectId == projectCode && r.StationCode == station.StationCode, ct);
        if (hasHistory)
        {
            station.IsArchived = true;
            station.Enabled = false;
            station.ArchivedAt = DateTime.UtcNow;
        }
        else
        {
            db.Stations.Remove(station);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}

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
        if (string.IsNullOrWhiteSpace(entity.StationCode))
        {
            entity.StationCode = $"ST-{(entity.Id > 0 ? entity.Id : Random.Shared.Next(1, 999999)):000}";
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await using var db = factory.CreateDbContext();
        var duplicateName = await db.Tasks.AnyAsync(
            t => t.Id != entity.Id && t.Name == entity.Name, ct);
        if (duplicateName)
        {
            throw new InvalidOperationException($"任务名称已存在：{entity.Name}。请使用其他名称。");
        }
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
    public async Task<BatchEntity?> FindRunningAsync(long taskId, string? projectId = null,
        string? stationCode = null, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.Batches.AsNoTracking()
            .Where(b => b.TaskId == taskId && b.Status == "running"
                && (projectId == null || b.ProjectId == projectId)
                && (stationCode == null || b.StationCode == stationCode))
            .OrderByDescending(b => b.StartedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BatchEntity> StartAsync(long taskId, long initialCounterValue,
        string projectId = "default", string stationCode = "", CancellationToken ct = default)
    {
        var batch = new BatchEntity
        {
            TaskId = taskId,
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? "default" : projectId.Trim(),
            StationCode = stationCode.Trim(),
            BatchNumber = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"),
            Status = "running",
            InitialCounterValue = initialCounterValue,
        };
        await using var db = factory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new InvalidOperationException($"任务不存在: {taskId}");
        if (string.IsNullOrWhiteSpace(batch.StationCode))
        {
            batch.StationCode = task.StationCode;
        }
        var batchNumberBase = batch.BatchNumber;
        var suffix = 1;
        while (await db.Batches.AnyAsync(b => b.TaskId == taskId && b.BatchNumber == batch.BatchNumber, ct))
        {
            batch.BatchNumber = $"{batchNumberBase}-{suffix++:00}";
        }
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
    /// <summary>导出历史记录 CSV（DAT-006）：UTF-8 BOM，兼容 Windows 表格工具和中文内容。</summary>
    public async Task ExportCsvAsync(
        RecordQuery query,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write,
            FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", new[]
        {
            "Id", "ProjectId", "StationCode", "TaskId", "BatchId", "RunType", "SourceRecordId", "StartedAt", "CompletedAt", "Status",
            "AlgorithmElapsedMs", "TotalElapsedMs", "PluginVersion", "ModelVersion", "WasCorrected",
        }));

        var pageIndex = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await QueryAsync(query, pageIndex, 500, ct);
            foreach (var record in page.Items)
            {
                await writer.WriteLineAsync(string.Join(",", new[]
                {
                    Csv(record.Id),
                    Csv(record.ProjectId),
                    Csv(record.StationCode),
                    Csv(record.TaskId),
                    Csv(record.BatchId),
                    Csv(record.RunType),
                    Csv(record.SourceRecordId),
                    Csv(record.StartedAt.ToString("O", CultureInfo.InvariantCulture)),
                    Csv(record.CompletedAt?.ToString("O", CultureInfo.InvariantCulture)),
                    Csv(record.Status),
                    Csv(record.AlgorithmElapsedMs.ToString(CultureInfo.InvariantCulture)),
                    Csv(record.TotalElapsedMs.ToString(CultureInfo.InvariantCulture)),
                    Csv(record.PluginVersion),
                    Csv(record.ModelVersion),
                    Csv(record.WasCorrected),
                }));
            }
            if (page.Items.Count == 0 || pageIndex + 1 >= page.TotalPages)
            {
                break;
            }
            pageIndex++;
        }
        await writer.FlushAsync(ct);
    }

    public async Task<InspectionRecordEntity> AddAsync(
        InspectionRecordEntity record,
        IReadOnlyList<Contracts.Results.CountingEvent>? countingEvents = null,
        IReadOnlyList<Contracts.Results.VisionEvent>? visionEvents = null,
        CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        if (string.IsNullOrWhiteSpace(record.ProjectId))
        {
            record.ProjectId = "default";
        }
        if (string.IsNullOrWhiteSpace(record.StationCode))
        {
            record.StationCode = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == record.TaskId)
                .Select(t => t.StationCode)
                .FirstOrDefaultAsync(ct) ?? $"ST-{record.TaskId:000}";
        }
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
        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            q = q.Where(r => r.ProjectId == query.ProjectId);
        }
        if (query.TaskId is { } taskId)
        {
            q = q.Where(r => r.TaskId == taskId);
        }
        if (!string.IsNullOrWhiteSpace(query.StationCode))
        {
            q = q.Where(r => r.StationCode == query.StationCode);
        }
        if (query.BatchId is { } batchId)
        {
            q = q.Where(r => r.BatchId == batchId);
        }
        if (!string.IsNullOrEmpty(query.Status))
        {
            q = q.Where(r => r.Status == query.Status);
        }
        if (!string.IsNullOrWhiteSpace(query.ModelVersion))
        {
            q = q.Where(r => r.ModelVersion == query.ModelVersion);
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

    /// <summary>删除检测记录及其关联事件/纠错，并返回需要由应用层删除的本地证据文件路径。</summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var record = await db.InspectionRecords.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException($"检测记录不存在: {id}");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPath(record.OriginalImagePath);
        AddPath(record.AnnotatedImagePath);

        var countingPaths = await db.CountingEvents.AsNoTracking()
            .Where(e => e.RecordId == id && e.EvidenceImagePath != null)
            .Select(e => e.EvidenceImagePath!)
            .ToListAsync(ct);
        foreach (var path in countingPaths)
        {
            AddPath(path);
        }

        var visionPaths = await db.VisionEvents.AsNoTracking()
            .Where(e => e.RecordId == id && e.EvidenceImagePath != null)
            .Select(e => e.EvidenceImagePath!)
            .ToListAsync(ct);
        foreach (var path in visionPaths)
        {
            AddPath(path);
        }

        await db.Corrections.Where(c => c.RecordId == id).ExecuteDeleteAsync(ct);
        await db.CountingEvents.Where(e => e.RecordId == id).ExecuteDeleteAsync(ct);
        await db.VisionEvents.Where(e => e.RecordId == id).ExecuteDeleteAsync(ct);
        db.InspectionRecords.Remove(record);
        await db.SaveChangesAsync(ct);
        return paths.ToArray();

        void AddPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }
    }

    /// <summary>删除全部检测记录及其关联事件/纠错，并返回需要删除的本地证据文件路径。</summary>
    public async Task<IReadOnlyList<string>> DeleteAllAsync(CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recordPaths = await db.InspectionRecords.AsNoTracking()
            .Select(r => new { r.OriginalImagePath, r.AnnotatedImagePath })
            .ToListAsync(ct);
        foreach (var item in recordPaths)
        {
            AddPath(item.OriginalImagePath);
            AddPath(item.AnnotatedImagePath);
        }

        var countingPaths = await db.CountingEvents.AsNoTracking()
            .Where(e => e.EvidenceImagePath != null)
            .Select(e => e.EvidenceImagePath!)
            .ToListAsync(ct);
        foreach (var path in countingPaths)
        {
            AddPath(path);
        }

        var visionPaths = await db.VisionEvents.AsNoTracking()
            .Where(e => e.EvidenceImagePath != null)
            .Select(e => e.EvidenceImagePath!)
            .ToListAsync(ct);
        foreach (var path in visionPaths)
        {
            AddPath(path);
        }

        await db.Corrections.ExecuteDeleteAsync(ct);
        await db.CountingEvents.ExecuteDeleteAsync(ct);
        await db.VisionEvents.ExecuteDeleteAsync(ct);
        await db.InspectionRecords.ExecuteDeleteAsync(ct);
        return paths.ToArray();

        void AddPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.InspectionRecords.CountAsync(ct);
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

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
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
