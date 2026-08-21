using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Application;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>SQLite 持久化：六表往返（DAT-001）、分页筛选（DAT-003）、纠错审计（CNT-S-010）。</summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly VisionDbContextFactory _factory;

    public PersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vw-test-{Guid.NewGuid():N}.db");
        _factory = VisionDbContextFactory.Create(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Task_Save_And_Reload_Roundtrips_Json()
    {
        var tasks = new TaskRepository(_factory);
        var saved = await tasks.SaveAsync(new TaskEntity
        {
            Name = "瓶盖计数",
            CameraProviderId = "image-folder",
            CameraDeviceId = @"D:\图片 目录",
            PluginId = "com.vision.sample-counter",
            SettingsJson = """{"minAreaPx":150}""",
            RulesJson = """[{"ruleId":"r1","kind":"countEquals","expectedCount":2}]""",
        });
        Assert.NotEqual(0, saved.Id);

        var reloaded = await tasks.FindAsync(saved.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("瓶盖计数", reloaded!.Name);
        Assert.Contains("minAreaPx", reloaded.SettingsJson);
        Assert.Contains("countEquals", reloaded.RulesJson);
    }

    [Fact]
    public async Task Task_Save_Duplicate_Name_Throws()
    {
        var tasks = new TaskRepository(_factory);
        await tasks.SaveAsync(new TaskEntity { Name = "唯一", CameraProviderId = "x", CameraDeviceId = "y", PluginId = "p" });
        await Assert.ThrowsAnyAsync<Exception>(() =>
            tasks.SaveAsync(new TaskEntity { Name = "唯一", CameraProviderId = "x", CameraDeviceId = "y", PluginId = "p" }));
    }

    [Fact]
    public async Task Record_With_Events_And_Paging()
    {
        var tasks = new TaskRepository(_factory);
        var task = await tasks.SaveAsync(new TaskEntity
        {
            Name = "分页任务",
            CameraProviderId = "image-folder",
            CameraDeviceId = "x",
            PluginId = "p",
        });
        var batches = new BatchRepository(_factory);
        var batch = await batches.StartAsync(task.Id, 0);

        var records = new RecordRepository(_factory);
        for (var i = 0; i < 7; i++)
        {
            await records.AddAsync(new InspectionRecordEntity
            {
                TaskId = task.Id,
                BatchId = batch.Id,
                Status = i % 2 == 0 ? "ok" : "ng",
                RawResultJson = "{}",
                FinalResultJson = "{}",
            },
            countingEvents:
            [
                new CountingEvent
                {
                    EventId = $"e{i}",
                    CounterId = "c1",
                    Type = CountingEventType.Accepted,
                    Delta = 1,
                },
            ]);
        }

        var all = await records.QueryAsync(new RecordQuery(TaskId: task.Id), pageIndex: 0, pageSize: 5);
        Assert.Equal(7, all.Total);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(2, all.TotalPages);

        var ngOnly = await records.QueryAsync(new RecordQuery(TaskId: task.Id, Status: "ng"));
        Assert.Equal(3, ngOnly.Total);

        var events = await records.ListCountingEventsAsync(batch.Id);
        Assert.Equal(7, events.Count);
        Assert.All(events, e => Assert.Equal("Accepted", e.EventType));

        var ended = await batches.EndAsync(batch.Id, 7);
        Assert.NotNull(ended);
        Assert.Equal(7, ended!.FinalCounterValue);
        Assert.Equal("completed", ended.Status);
    }

    [Fact]
    public async Task Correction_Requires_Reason_And_Marks_Record()
    {
        var tasks = new TaskRepository(_factory);
        var task = await tasks.SaveAsync(new TaskEntity
        {
            Name = "纠错任务",
            CameraProviderId = "image-folder",
            CameraDeviceId = "x",
            PluginId = "p",
        });
        var records = new RecordRepository(_factory);
        var record = await records.AddAsync(new InspectionRecordEntity
        {
            TaskId = task.Id,
            Status = "ng",
            FinalResultJson = """{"status":"ng"}""",
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            records.AddCorrectionAsync(record.Id, "{}", "{}", "", "op"));

        var correction = await records.AddCorrectionAsync(
            record.Id, """{"status":"ng"}""", """{"status":"ok"}""", "实际为合格品，人工复判", "张工");
        Assert.NotEqual(0, correction.Id);

        var reloaded = await records.FindAsync(record.Id);
        Assert.True(reloaded!.WasCorrected);
        var history = await records.ListCorrectionsAsync(record.Id);
        Assert.Single(history);
        Assert.Equal("张工", history[0].OperatorName);
        Assert.Equal("实际为合格品，人工复判", history[0].Reason);
    }

    [Fact]
    public async Task BatchService_Resumes_Running_Batch_From_Events()
    {
        var tasks = new TaskRepository(_factory);
        var task = await tasks.SaveAsync(new TaskEntity
        {
            Name = "恢复批次",
            CameraProviderId = "video-file",
            CameraDeviceId = "x.avi",
            PluginId = "p",
        });
        var batches = new BatchRepository(_factory);
        var records = new RecordRepository(_factory);
        var batch = await batches.StartAsync(task.Id, 0);
        await records.AppendCountingEventAsync(new CountingEvent
        {
            EventId = "resume-1",
            CounterId = "c1",
            Type = CountingEventType.CrossedLine,
            Direction = "forward",
            Delta = 1,
        }, batch.Id);

        var counting = new CountingService("c1");
        var resumed = await new BatchService(batches).ResumeOrStartAsync(task.Id, counting, records);

        Assert.Equal(batch.Id, resumed.Id);
        Assert.Equal(1, counting.State.CurrentTotal);
        Assert.Equal(1, counting.State.ForwardTotal);
    }

    [Fact]
    public async Task History_Csv_Uses_Utf8_Bom_And_Escapes_Chinese()
    {
        var tasks = new TaskRepository(_factory);
        var task = await tasks.SaveAsync(new TaskEntity
        {
            Name = "CSV 中文任务",
            CameraProviderId = "image-folder",
            CameraDeviceId = "中文目录",
            PluginId = "p",
        });
        var records = new RecordRepository(_factory);
        await records.AddAsync(new InspectionRecordEntity
        {
            TaskId = task.Id,
            Status = "ng",
            PluginVersion = "插件,1",
            RawResultJson = "{}",
            FinalResultJson = "{}",
        });
        var csv = Path.Combine(Path.GetTempPath(), $"vw-history-{Guid.NewGuid():N}.csv");
        try
        {
            await records.ExportCsvAsync(new RecordQuery(TaskId: task.Id), csv);
            var bytes = await File.ReadAllBytesAsync(csv);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
            var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("\"插件,1\"", text);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task Database_Backup_And_Restore_Rolls_Back_New_Record()
    {
        var tasks = new TaskRepository(_factory);
        var first = await tasks.SaveAsync(new TaskEntity
        {
            Name = "备份前",
            CameraProviderId = "image-folder",
            CameraDeviceId = "x",
            PluginId = "p",
        });
        var backup = Path.Combine(Path.GetTempPath(), $"vw-backup-{Guid.NewGuid():N}.db");
        try
        {
            new DatabaseBackupService(_factory).BackupTo(backup);
            await tasks.SaveAsync(new TaskEntity
            {
                Name = "备份后",
                CameraProviderId = "image-folder",
                CameraDeviceId = "x",
                PluginId = "p",
            });
            new DatabaseBackupService(_factory).RestoreFrom(backup);
            var restored = await tasks.ListAsync();
            Assert.Single(restored);
            Assert.Equal(first.Id, restored[0].Id);
            Assert.Equal("备份前", restored[0].Name);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public async Task Project_Station_Code_Is_Unique_Per_Project_And_History_Is_Archived()
    {
        var projects = new ProjectStationRepository(_factory);
        var project = await projects.SaveProjectAsync(new ProjectEntity
        {
            ProjectCode = "line-a",
            Name = "产线 A",
        });
        var station = await projects.SaveStationAsync(new StationEntity
        {
            ProjectId = project.Id,
            StationCode = "ST-001",
            Name = "入口工位",
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => projects.SaveStationAsync(new StationEntity
        {
            ProjectId = project.Id,
            StationCode = "st-001",
            Name = "重复编号",
        }));

        var task = await new TaskRepository(_factory).SaveAsync(new TaskEntity
        {
            StationCode = "ST-001",
            Name = "入口配方",
            CameraProviderId = "image-folder",
            CameraDeviceId = "images",
            PluginId = "p",
        });
        var record = await new RecordRepository(_factory).AddAsync(new InspectionRecordEntity
        {
            ProjectId = "line-a",
            StationCode = "ST-001",
            TaskId = task.Id,
            ModelVersion = "yolo11-seg-v1",
            Status = "ok",
        });
        Assert.True(await projects.ArchiveOrDeleteStationAsync(station.Id));
        Assert.Empty(await projects.ListStationsAsync(project.Id));
        Assert.Single(await projects.ListStationsAsync(project.Id, includeArchived: true));

        var page = await new RecordRepository(_factory).QueryAsync(new RecordQuery(
            ProjectId: "line-a", StationCode: "ST-001", ModelVersion: "yolo11-seg-v1"));
        Assert.Single(page.Items);
        Assert.Equal(record.Id, page.Items[0].Id);
        Assert.Equal("line-a", page.Items[0].ProjectId);
    }
}
