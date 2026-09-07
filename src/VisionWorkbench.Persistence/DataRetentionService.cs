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
        await using var db = factory.CreateDbContext();
        var candidates = await db.InspectionRecords
            .Where(x => x.StartedAt < now.AddDays(-Math.Min(policy.OkDays, policy.NgDays)))
            .ToListAsync(ct);
        var protectedCount = candidates.Count(HasEvidenceOrReview);
        return new RetentionPreview(candidates.Count(x => IsExpired(x, policy, now) && !HasEvidenceOrReview(x)),
            protectedCount, now.AddDays(-Math.Min(policy.OkDays, policy.NgDays)));
    }

    public async Task<int> DeleteBatchAsync(RetentionPolicy policy, int batchSize = 100, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        await using var db = factory.CreateDbContext();
        var candidates = await db.InspectionRecords
            .Where(x => x.StartedAt < now.AddDays(-Math.Min(policy.OkDays, policy.NgDays)))
            .OrderBy(x => x.Id).Take(Math.Clamp(batchSize * 4, 1, 4000)).ToListAsync(ct);
        var deletable = candidates.Where(x => IsExpired(x, policy, now) && !HasEvidenceOrReview(x))
            .Take(Math.Clamp(batchSize, 1, 1000)).ToArray();
        db.InspectionRecords.RemoveRange(deletable);
        return await db.SaveChangesAsync(ct);
    }

    private static bool IsExpired(InspectionRecordEntity item, RetentionPolicy policy, DateTime now) =>
        item.StartedAt < now.AddDays(-(
            string.Equals(item.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(1, policy.OkDays)
                : Math.Max(1, policy.NgDays)));

    private static bool HasEvidenceOrReview(InspectionRecordEntity item) => item.WasCorrected || string.Equals(item.Status, "review_required", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(item.AnnotatedImagePath);
}
