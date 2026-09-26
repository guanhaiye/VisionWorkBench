using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

public sealed record RetentionPolicy(int OkDays = 180, int NgDays = 365, int AuditDays = 730, int LogDays = 30);
public sealed record RetentionPreview(int CandidateRecords, int ProtectedRecords, DateTime CutoffUtc);

/// <summary>数据保留清理：先预览，跳过有复核/纠错/证据引用的记录，再分批删除并由调用方审计。</summary>
public sealed class DataRetentionService(VisionDbContextFactory factory)
{
    public async Task<RetentionPreview> PreviewAsync(RetentionPolicy policy, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var cutoff = now.AddDays(-Math.Min(Math.Max(1, policy.OkDays), Math.Max(1, policy.NgDays)));
        await using var db = factory.CreateDbContext();
        var candidates = db.InspectionRecords.AsNoTracking().Where(x => x.StartedAt < cutoff);
        var unprotected = WithoutProtectedReferences(db, candidates);
        var protectedCount = await candidates.CountAsync(ct) - await unprotected.CountAsync(ct);
        return new RetentionPreview(await Expired(unprotected, policy, now).CountAsync(ct), protectedCount, cutoff);
    }

    public async Task<int> DeleteBatchAsync(RetentionPolicy policy, int batchSize = 100, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Filter before Take: old protected rows must not starve later eligible rows.
        var ids = await Expired(WithoutProtectedReferences(db, db.InspectionRecords), policy, now)
            .OrderBy(x => x.Id).Take(Math.Clamp(batchSize, 1, 1000)).Select(x => x.Id).ToListAsync(ct);
        var deleted = await db.InspectionRecords.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    private static IQueryable<InspectionRecordEntity> Expired(IQueryable<InspectionRecordEntity> query, RetentionPolicy policy, DateTime now)
    {
        var okCutoff = now.AddDays(-Math.Max(1, policy.OkDays));
        var ngCutoff = now.AddDays(-Math.Max(1, policy.NgDays));
        return query.Where(x => x.Status.ToLower() == "ok" ? x.StartedAt < okCutoff : x.StartedAt < ngCutoff);
    }

    private static IQueryable<InspectionRecordEntity> WithoutProtectedReferences(VisionDbContext db, IQueryable<InspectionRecordEntity> query) =>
        query.Where(x => !x.WasCorrected && x.Status.ToLower() != "review_required"
            && (x.OriginalImagePath == null || x.OriginalImagePath.Trim() == "")
            && (x.AnnotatedImagePath == null || x.AnnotatedImagePath.Trim() == "")
            && !db.Corrections.Any(item => item.RecordId == x.Id)
            && !db.CountingEvents.Any(item => item.RecordId == x.Id)
            && !db.VisionEvents.Any(item => item.RecordId == x.Id)
            && !db.SopStepResults.Any(item => item.InspectionRecordId == x.Id)
            && !db.InspectionRecords.Any(item => item.SourceRecordId == x.Id));
}