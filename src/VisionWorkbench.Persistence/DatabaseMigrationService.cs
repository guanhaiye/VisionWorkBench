using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

public sealed record DatabaseSchemaStatus(string CurrentVersion, bool IntegrityOk, DateTime CheckedAtUtc);
public sealed record DatabaseMigrationResult(string FromVersion, string ToVersion, bool Changed, DateTime AppliedAtUtc);

/// <summary>集中管理数据库版本和启动后完整性检查；所有新版本迁移应在此处按序登记。</summary>
public sealed class DatabaseMigrationService(VisionDbContextFactory factory)
{
    public const string CurrentVersion = "20260906-commercial-foundation";

    public async Task<DatabaseSchemaStatus> CheckAsync(CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT Version FROM SchemaMigrations ORDER BY AppliedAtUtc DESC LIMIT 1";
        var version = Convert.ToString(await versionCommand.ExecuteScalarAsync(ct).ConfigureAwait(false)) ?? "legacy";

        await using var integrityCommand = connection.CreateCommand();
        integrityCommand.CommandText = "PRAGMA integrity_check";
        var integrity = Convert.ToString(await integrityCommand.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return new DatabaseSchemaStatus(version, string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase), DateTime.UtcNow);
    }

    /// <summary>Applies the registered commercial baseline atomically. A caller may supply a pre-migration backup callback.</summary>
    public async Task<DatabaseMigrationResult> ApplyPendingAsync(
        Func<CancellationToken, Task>? preMigrationBackup = null,
        AuditService? audit = null,
        string actor = "system",
        CancellationToken ct = default)
    {
        var before = await CheckAsync(ct);
        if (!before.IntegrityOk) throw new InvalidOperationException("DB-001 database integrity check failed");
        if (string.Equals(before.CurrentVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase))
            return new DatabaseMigrationResult(before.CurrentVersion, CurrentVersion, false, DateTime.UtcNow);
        if (preMigrationBackup is not null) await preMigrationBackup(ct);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS SchemaMigrations (Version TEXT NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY, AppliedAtUtc TEXT NOT NULL, Description TEXT NOT NULL);", ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO SchemaMigrations(Version, AppliedAtUtc, Description) VALUES ({0}, CURRENT_TIMESTAMP, {1});",
            [CurrentVersion, "VisionWorkbench commercial foundation"], ct);
        await transaction.CommitAsync(ct);
        if (audit is not null)
            await audit.RecordAsync("database.migrate", "database", factory.DbPath, actor,
                detailsJson: $"{{\"from\":\"{before.CurrentVersion}\",\"to\":\"{CurrentVersion}\"}}", cancellationToken: ct);
        return new DatabaseMigrationResult(before.CurrentVersion, CurrentVersion, true, DateTime.UtcNow);
    }
}
