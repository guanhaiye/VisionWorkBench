using VisionWorkbench.Contracts.Results;
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
}
