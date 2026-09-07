using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public sealed record FeedbackCandidate(long RecordId, string Status, string? ImagePath, DateTime StartedAtUtc);

/// <summary>Preserves human corrections and provides the durable input for versioned retraining datasets.</summary>
public sealed class FeedbackService(VisionDbContextFactory factory)
{
    public async Task<IReadOnlyList<FeedbackCandidate>> ListCandidatesAsync(int limit = 500, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        return await db.InspectionRecords.AsNoTracking()
            .Where(x => !x.WasCorrected && (x.Status == "ng" || x.Status == "review_required"))
            .OrderBy(x => x.StartedAt).Take(Math.Clamp(limit, 1, 5000))
            .Select(x => new FeedbackCandidate(x.Id, x.Status, x.AnnotatedImagePath ?? x.OriginalImagePath, x.StartedAt))
            .ToListAsync(ct);
    }

    public async Task<CorrectionEntity> ApplyCorrectionAsync(
        long recordId, string beforeJson, string afterJson, string reason, string operatorName, CancellationToken ct = default)
    {
        ValidateJson(beforeJson); ValidateJson(afterJson);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Correction reason is required", nameof(reason));
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var record = await db.InspectionRecords.SingleOrDefaultAsync(x => x.Id == recordId, ct)
            ?? throw new InvalidOperationException("Inspection record does not exist");
        var correction = new CorrectionEntity
        {
            RecordId = record.Id, BeforeJson = beforeJson, AfterJson = afterJson,
            Reason = reason.Trim(), OperatorName = operatorName.Trim(), CorrectedAt = DateTime.UtcNow,
        };
        record.WasCorrected = true;
        db.Corrections.Add(correction);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return correction;
    }

    private static void ValidateJson(string value)
    {
        try { JsonDocument.Parse(value).Dispose(); }
        catch (JsonException ex) { throw new InvalidDataException("Feedback JSON is invalid", ex); }
    }
}

public sealed class DatasetVersionService(VisionDbContextFactory factory)
{
    public async Task<DatasetVersionEntity> CreateAsync(string datasetCode, string manifestPath, int trainingCount, int validationCount, string actor, CancellationToken ct = default)
    {
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Dataset manifest does not exist", manifestPath);
        if (trainingCount < 0 || validationCount < 0) throw new ArgumentOutOfRangeException(nameof(trainingCount));
        await using var db = factory.CreateDbContext();
        var version = (await db.DatasetVersions.Where(x => x.DatasetCode == datasetCode).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        await using var stream = File.OpenRead(manifestPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        var item = new DatasetVersionEntity { DatasetCode = datasetCode, Version = version, Sha256 = hash, TrainingCount = trainingCount, ValidationCount = validationCount, CreatedBy = actor };
        db.DatasetVersions.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }
}
