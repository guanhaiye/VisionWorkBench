using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class PersistenceSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vw-persistence-safety-" + Guid.NewGuid().ToString("N"));
    private readonly VisionDbContextFactory _factory;

    public PersistenceSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _factory = VisionDbContextFactory.Create(Path.Combine(_root, "visionworkbench.db"));
    }

    [Fact]
    public async Task Backup_Restores_Config_Files_To_Config_Directory()
    {
        var config = Path.Combine(_root, "Config");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "communication.json");
        await File.WriteAllTextAsync(path, "original");
        var packages = new BackupPackageService(_factory, defaultDataDirectory: _root);
        var package = Path.Combine(_root, "settings.vwbackup");
        await packages.CreateAsync(package);
        await File.WriteAllTextAsync(path, "changed");
        await packages.RestoreAsync(package);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(_root, "communication.json")));
    }

    [Fact]
    public async Task Restore_File_Failure_Rolls_Back_Files_And_Preserves_Current_Database()
    {
        var config = Path.Combine(_root, "Config");
        Directory.CreateDirectory(config);
        var settings = Path.Combine(_root, "settings.json");
        var blocked = Path.Combine(config, "blocked.json");
        await File.WriteAllTextAsync(settings, "backup settings");
        await File.WriteAllTextAsync(blocked, "backup config");
        var packages = new BackupPackageService(_factory, settings, _root);
        var package = Path.Combine(_root, "rollback.vwbackup");
        await packages.CreateAsync(package);
        await File.WriteAllTextAsync(settings, "current settings");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        await SeedTaskAsync("created after backup");

        await Assert.ThrowsAnyAsync<Exception>(() => packages.RestoreAsync(package));

        Assert.Equal("current settings", await File.ReadAllTextAsync(settings));
        Assert.Single(await new TaskRepository(_factory).ListAsync());
    }

    [Fact]
    public async Task Cancelled_Backup_Does_Not_Destroy_Existing_Package()
    {
        var path = Path.Combine(_root, "existing.vwbackup");
        await File.WriteAllTextAsync(path, "previous backup");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupPackageService(_factory).CreateAsync(path, cancellationToken: cancelled.Token));
        Assert.Equal("previous backup", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Retention_Skips_Protected_Prefix_And_Protects_Original_Image_And_Event_References()
    {
        var task = await SeedTaskAsync("retention");
        await using (var db = _factory.CreateDbContext())
        {
            for (var index = 0; index < 10; index++)
                db.InspectionRecords.Add(new InspectionRecordEntity { TaskId = task.Id, Status = "ok", StartedAt = DateTime.UtcNow.AddDays(-20), OriginalImagePath = "original.png" });
            var referenced = new InspectionRecordEntity { TaskId = task.Id, Status = "ok", StartedAt = DateTime.UtcNow.AddDays(-20) };
            db.InspectionRecords.Add(referenced);
            db.InspectionRecords.Add(new InspectionRecordEntity { TaskId = task.Id, Status = "ok", StartedAt = DateTime.UtcNow.AddDays(-20) });
            await db.SaveChangesAsync();
            db.CountingEvents.Add(new CountingEventEntity { RecordId = referenced.Id, EvidenceImagePath = "event.png" });
            await db.SaveChangesAsync();
        }
        var service = new DataRetentionService(_factory);
        var policy = new RetentionPolicy(OkDays: 1, NgDays: 1);
        var preview = await service.PreviewAsync(policy);
        Assert.Equal(11, preview.ProtectedRecords);
        Assert.Equal(1, preview.CandidateRecords);
        Assert.Equal(1, await service.DeleteBatchAsync(policy, batchSize: 1));
        Assert.Equal(0, await service.DeleteBatchAsync(policy, batchSize: 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_Delete_Failure_Preserves_Associated_Events(bool deleteAll)
    {
        var task = await SeedTaskAsync("delete");
        var record = await new RecordRepository(_factory).AddAsync(new InspectionRecordEntity { TaskId = task.Id });
        await using (var db = _factory.CreateDbContext())
        {
            db.CountingEvents.Add(new CountingEventEntity { RecordId = record.Id });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER prevent_record_delete BEFORE DELETE ON InspectionRecords BEGIN SELECT RAISE(ABORT, 'test delete failure'); END;");
        }
        var repository = new RecordRepository(_factory);
        await Assert.ThrowsAnyAsync<Exception>(() => deleteAll ? repository.DeleteAllAsync() : repository.DeleteAsync(record.Id));
        await using var verify = _factory.CreateDbContext();
        Assert.Single(await verify.InspectionRecords.ToListAsync());
        Assert.Single(await verify.CountingEvents.ToListAsync());
    }

    [Fact]
    public async Task Sop_Finalization_Persists_First_Final_Step_Snapshot()
    {
        var task = await SeedTaskAsync("sop");
        var repository = new SopRunRepository(_factory);
        var run = await repository.StartAsync(new SopRunEntity { TaskId = task.Id, CycleId = "cycle" });
        var snapshot = new SopSnapshot
        {
            Status = SopRunStatus.CompletedOk, CurrentStepOrder = 1,
            Steps = [new SopStepSnapshot { StepId = "first", Name = "first", Status = SopStepStatus.Completed }],
        };
        Assert.True(await repository.PrepareFinalizationAsync(run.Id, snapshot, "ok", "{}", "{}"));
        await using var db = _factory.CreateDbContext();
        Assert.Equal("Completed", (await db.SopStepResults.SingleAsync()).Status);
    }

    [Fact]
    public async Task Repeated_Alert_Recovery_Preserves_Every_Occurrence()
    {
        var health = new HealthService(_factory);
        for (var index = 0; index < 3; index++)
        {
            await health.RaiseOrRefreshAlertAsync("CAM-001", "warning", "camera unavailable");
            Assert.True(await health.RecoverAlertAsync("CAM-001"));
        }
        await using var db = _factory.CreateDbContext();
        Assert.Equal(3, await db.Alerts.CountAsync(x => x.Status == "recovered"));
    }

    [Fact]
    public async Task Database_Path_With_Semicolon_Is_Not_A_Connection_String()
    {
        var path = Path.Combine(_root, "customer;station.db");
        var factory = VisionDbContextFactory.Create(path);
        await using var db = factory.CreateDbContext();
        Assert.True(await db.Database.CanConnectAsync());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Communication_Request_Command_And_Renewed_Expiry_Are_Preserved()
    {
        var store = new CommunicationRequestStore(_factory);
        var first = await store.BeginAsync("p", "c", "r", "capture", "{}");
        await store.CompleteAsync(first.Request!.Id, "{}");
        Assert.Equal(CommunicationRequestState.Conflict, (await store.BeginAsync("p", "c", "r", "stop", "{}")).State);
        await using (var db = _factory.CreateDbContext())
            await db.CommunicationRequests.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddDays(-1)));
        var retried = await store.BeginAsync("p", "c", "r", "capture", "{}");
        Assert.Equal(CommunicationRequestState.New, retried.State);
        Assert.True(retried.Request!.ExpiresAtUtc > DateTime.UtcNow.AddDays(6));
        await using (var db = _factory.CreateDbContext())
            await db.CommunicationRequests.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddDays(-1)));
        Assert.Equal(0, await store.PurgeExpiredAsync());
    }
    [Fact]
    public async Task Sop_Finalization_Failure_Does_Not_Publish_Partial_Final_Result()
    {
        var task = await SeedTaskAsync("sop rollback");
        var repository = new SopRunRepository(_factory);
        var run = await repository.StartAsync(new SopRunEntity { TaskId = task.Id, CycleId = "failed-cycle" });
        await using (var db = _factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER prevent_step_insert BEFORE INSERT ON SopStepResults BEGIN SELECT RAISE(ABORT, 'test insert failure'); END;");
        var snapshot = new SopSnapshot
        {
            Status = SopRunStatus.CompletedOk,
            Steps = [new SopStepSnapshot { StepId = "first", Name = "first", Status = SopStepStatus.Completed }],
        };
        await Assert.ThrowsAnyAsync<Exception>(() => repository.PrepareFinalizationAsync(run.Id, snapshot, "ok", "{}", "{}"));
        await using var verify = _factory.CreateDbContext();
        Assert.True((await verify.SopRuns.SingleAsync()).FinalizedAtUtc is null);
    }
    [Fact]
    public async Task Retention_Keeps_Last_Valid_Backup_When_Newest_Is_Corrupt()
    {
        var directory = Path.Combine(_root, "retention-backups");
        Directory.CreateDirectory(directory);
        var valid = Path.Combine(directory, "valid.vwbackup");
        await new BackupPackageService(_factory).CreateAsync(valid);
        File.SetLastWriteTimeUtc(valid, DateTime.UtcNow.AddDays(-2));
        var corrupt = Path.Combine(directory, "corrupt.vwbackup");
        await File.WriteAllTextAsync(corrupt, "incomplete archive");
        var result = new BackupRetentionService().Apply(directory, maxCount: 1);
        Assert.True(File.Exists(valid));
        Assert.Equal(2, result.Kept.Count);
    }
    private Task<TaskEntity> SeedTaskAsync(string name) => new TaskRepository(_factory).SaveAsync(new TaskEntity
    {
        Name = name, CameraProviderId = "test", CameraDeviceId = "test", PluginId = "test",
    });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}