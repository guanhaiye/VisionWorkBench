using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

public sealed record HealthSnapshotInput(
    string State,
    double CpuUsage,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    ulong GpuUsedBytes,
    ulong GpuTotalBytes,
    ulong FreeDiskBytes,
    object? Details = null);

/// <summary>方案 §10：健康采样和告警生命周期的持久化基础。</summary>
public sealed class HealthService(VisionDbContextFactory factory)
{
    public async Task<HealthSnapshotEntity> RecordSnapshotAsync(HealthSnapshotInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var db = factory.CreateDbContext();
        var item = new HealthSnapshotEntity
        {
            State = string.IsNullOrWhiteSpace(input.State) ? "Healthy" : input.State,
            CpuUsage = Math.Clamp(input.CpuUsage, 0, 100),
            MemoryUsedBytes = input.MemoryUsedBytes,
            MemoryTotalBytes = input.MemoryTotalBytes,
            GpuUsedBytes = input.GpuUsedBytes,
            GpuTotalBytes = input.GpuTotalBytes,
            FreeDiskBytes = input.FreeDiskBytes,
            DetailsJson = input.Details is null ? "{}" : JsonSerializer.Serialize(input.Details),
            CapturedAtUtc = DateTime.UtcNow,
        };
        db.HealthSnapshots.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<AlertEntity> RaiseOrRefreshAlertAsync(
        string code, string severity, string title, object? details = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var alert = await db.Alerts.FirstOrDefaultAsync(item => item.Code == code && item.Status == "active", cancellationToken);
        if (alert is null)
        {
            alert = new AlertEntity
            {
                Code = code.Trim(), Severity = severity.Trim(), Title = title.Trim(),
                DetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details),
                FirstSeenAtUtc = now, LastSeenAtUtc = now, Status = "active",
            };
            db.Alerts.Add(alert);
        }
        else
        {
            alert.Severity = severity.Trim();
            alert.Title = title.Trim();
            alert.DetailsJson = details is null ? alert.DetailsJson : JsonSerializer.Serialize(details);
            alert.LastSeenAtUtc = now;
            alert.RecoveredAtUtc = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return alert;
    }

    public async Task<bool> RecoverAlertAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var alert = await db.Alerts.FirstOrDefaultAsync(item => item.Code == code && item.Status == "active", cancellationToken);
        if (alert is null) return false;
        alert.Status = "recovered";
        alert.RecoveredAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AcknowledgeAlertAsync(long id, string actorName, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var alert = await db.Alerts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (alert is null) return false;
        alert.AcknowledgedAtUtc = DateTime.UtcNow;
        alert.AcknowledgedBy = actorName.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
