using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Cameras.Files;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace VisionWorkbench.Tests;

/// <summary>
/// 服务层端到端（文档 §24 冒烟 1~10 等价物）：
/// ImageFolderProvider → sample-counter Worker → 规则判定 → SQLite 落库 → 可再查询。
/// </summary>
public sealed class ServiceEndToEndTests(ITestOutputHelper output)
{
    /// <summary>把服务日志透传到 xUnit 输出（定位后台落库失败）。</summary>
    private sealed class OutputLogger<T>(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            output.WriteLine($"[svc:{level}] {formatter(state, exception)}" +
                (exception is null ? "" : $" | {exception.Message}"));
    }

    private sealed class E2EContext : IAsyncDisposable
    {
        public required string DataDir { get; init; }
        public required TaskRepository Tasks { get; init; }
        public required RecordRepository Records { get; init; }
        public required BatchRepository Batches { get; init; }
        public required TempImageStore TempImages { get; init; }
        public required AlgorithmManager Manager { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Manager.ShutdownAllAsync();
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string CopySample(string name, string destDir)
    {
        var src = Path.Combine(TestPaths.SamplesStaticCount, name);
        Assert.True(File.Exists(src), $"样本缺失: {src}");
        var dest = Path.Combine(destDir, name);
        File.Copy(src, dest);
        return dest;
    }

    private static async Task<E2EContext> CreateContextAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"vw-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var factory = VisionDbContextFactory.Create(Path.Combine(dataDir, "test.db"));
        return new E2EContext
        {
            DataDir = dataDir,
            Tasks = new TaskRepository(factory),
            Records = new RecordRepository(factory),
            Batches = new BatchRepository(factory),
            TempImages = new TempImageStore(Path.Combine(dataDir, "temp-images")).Initialize(),
            Manager = new AlgorithmManager(new AlgorithmManagerOptions
            {
                PluginsRoot = Path.Combine(TestPaths.Root, "workers"),
                PythonExecutable = TestPaths.VenvPython,
                LogsDirectory = Path.Combine(dataDir, "logs"),
            }, NullLoggerFactory.Instance.CreateLogger<AlgorithmManager>()),
        };
    }

    /// <summary>跑一轮完整检测：单图目录 → Worker → 规则 → 落库，返回 (记录数, 判定列表)。</summary>
    private async Task<List<RecordCompletedEventArgs>> RunOnceAsync(
        E2EContext ctx, string imagesDir, IReadOnlyList<InspectionRule> rules)
    {
        var task = await ctx.Tasks.SaveAsync(new TaskEntity
        {
            Name = $"e2e-{Guid.NewGuid():N}",
            CameraProviderId = "image-folder",
            CameraDeviceId = imagesDir,
            PluginId = "com.vision.sample-counter",
        });
        var recipe = new Recipe
        {
            Name = task.Name,
            CameraProviderId = "image-folder",
            CameraDeviceId = imagesDir,
            PluginId = "com.vision.sample-counter",
            Rules = rules,
        };

        var camera = await new ImageFolderProvider().CreateSessionAsync(
            new CameraDescriptor
            {
                ProviderId = "image-folder",
                DeviceId = imagesDir,
                DisplayName = imagesDir,
            }, CancellationToken.None);
        await camera.OpenAsync(new CameraOpenOptions { FrameIntervalMs = 0 }, CancellationToken.None);

        var algorithm = await ctx.Manager.CreateSessionAsync("com.vision.sample-counter", CancellationToken.None);

        var batch = await ctx.Batches.StartAsync(task.Id, 0);
        var run = new DetectionRunService(ctx.Records, ctx.TempImages,
            new OutputLogger<DetectionRunService>(output), $"task-{task.Id}");
        var results = new List<RecordCompletedEventArgs>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new List<string>();
        run.RecordCompleted += (_, e) => { lock (results) { results.Add(e); } };
        run.Faulted += (_, e) => { lock (faulted) { faulted.Add($"[{e.Source}] {e.Code}: {e.Message}"); } };
        run.SourceCompleted += (_, _) => finished.TrySetResult();

        try
        {
            await run.StartAsync(recipe, task.Id, camera, algorithm, batch.Id,
                FrameRoutingStrategy.Bounded); // 图片目录逐帧不漏（FRM-003）
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(90));
            await run.StopAsync();
            var total = run.Counting.State.CurrentTotal;
            await ctx.Batches.EndAsync(batch.Id, total);
        }
        finally
        {
            await run.DisposeAsync();
            await camera.DisposeAsync();
        }
        lock (faulted)
        {
            foreach (var fault in faulted)
            {
                output.WriteLine($"[fault] {fault}");
            }
        }
        return results;
    }

    [Fact]
    public async Task Ok_Path_Count_Matches_Truth()
    {
        if (!TestPaths.VenvExists)
        {
            return; // 环境无 Python 时优雅跳过
        }

        var imagesDir = Path.Combine(Path.GetTempPath(), $"vw-imgs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(imagesDir);
        try
        {
            CopySample("img_05_n05.png", imagesDir); // 真值 5

            await using var ctx = await CreateContextAsync();
            var results = await RunOnceAsync(ctx, imagesDir,
            [
                new InspectionRule { RuleId = "exact-5", Kind = RuleKind.CountEquals, ExpectedCount = 5 },
            ]);

            var completed = results;
            Assert.Single(completed);
            Assert.Equal(DecisionStatus.Ok, completed[0].Decision.Status);
            Assert.Equal(5, completed[0].Output.GetCount());

            // 落库可再查询（DAT-001）：1 条 ok 记录，OK 不保存证据图（DAT-002/20.3）
            var page = await ctx.Records.QueryAsync(new RecordQuery(Status: "ok"));
            Assert.Equal(1, page.Total);
            var record = page.Items[0];
            Assert.Null(record.OriginalImagePath);
            Assert.Null(record.AnnotatedImagePath);
            Assert.True(record.AlgorithmElapsedMs > 0);
            Assert.Contains("exact-5", record.FinalResultJson);

            output.WriteLine($"OK 路径：count={completed[0].Output.GetCount()} algoms={record.AlgorithmElapsedMs:0.#}");
        }
        finally
        {
            Directory.Delete(imagesDir, recursive: true);
        }
    }

    [Fact]
    public async Task Ng_Path_Saves_Evidence_Images()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        var imagesDir = Path.Combine(Path.GetTempPath(), $"vw-imgs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(imagesDir);
        try
        {
            CopySample("img_05_n05.png", imagesDir);

            await using var ctx = await CreateContextAsync();
            var results = await RunOnceAsync(ctx, imagesDir,
            [
                new InspectionRule { RuleId = "wrong-4", Kind = RuleKind.CountEquals, ExpectedCount = 4 },
            ]);

            Assert.Single(results);
            Assert.Equal(DecisionStatus.Ng, results[0].Decision.Status);

            // NG 落库 + 证据图存在（DAT-002）
            var page = await ctx.Records.QueryAsync(new RecordQuery(Status: "ng"));
            Assert.Equal(1, page.Total);
            var record = page.Items[0];
            Assert.NotNull(record.OriginalImagePath);
            Assert.NotNull(record.AnnotatedImagePath);
            Assert.True(File.Exists(record.OriginalImagePath!), $"原图缺失: {record.OriginalImagePath}");
            Assert.True(File.Exists(record.AnnotatedImagePath!), $"标注图缺失: {record.AnnotatedImagePath}");
            output.WriteLine($"NG 证据: {record.AnnotatedImagePath}");

            // 临时图片已清理（FRM-005）
            Assert.Equal(0, ctx.TempImages.CountFiles());
        }
        finally
        {
            Directory.Delete(imagesDir, recursive: true);
        }
    }

    [Fact]
    public async Task Multi_Image_Batch_Processes_All_And_Counts()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        var imagesDir = Path.Combine(Path.GetTempPath(), $"vw-imgs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(imagesDir);
        try
        {
            CopySample("img_00_n00.png", imagesDir);
            CopySample("img_05_n05.png", imagesDir);
            CopySample("img_09_n10.png", imagesDir);

            await using var ctx = await CreateContextAsync();
            var results = await RunOnceAsync(ctx, imagesDir,
            [
                new InspectionRule { RuleId = "min-0", Kind = RuleKind.CountAtLeast, ExpectedCount = 0 },
            ]);

            Assert.Equal(3, results.Count);
            var counts = results.Select(r => r.Output.GetCount()).OrderBy(c => c).ToArray();
            Assert.Equal([0, 5, 10], counts); // 真值按图核验

            var page = await ctx.Records.QueryAsync(new RecordQuery());
            Assert.Equal(3, page.Total);
            output.WriteLine($"批量：{results.Count} 帧，counts=[{string.Join(",", counts)}]");
        }
        finally
        {
            Directory.Delete(imagesDir, recursive: true);
        }
    }

    [Fact]
    public async Task Manual_Adjustment_Persists_Corrected_Event()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        var imagesDir = Path.Combine(Path.GetTempPath(), $"vw-imgs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(imagesDir);
        try
        {
            CopySample("img_05_n05.png", imagesDir);
            await using var ctx = await CreateContextAsync();

            var task = await ctx.Tasks.SaveAsync(new TaskEntity
            {
                Name = $"adj-{Guid.NewGuid():N}",
                CameraProviderId = "image-folder",
                CameraDeviceId = imagesDir,
                PluginId = "com.vision.sample-counter",
            });
            var run = new DetectionRunService(ctx.Records, ctx.TempImages,
                new OutputLogger<DetectionRunService>(output), $"task-{task.Id}");

            // 无原因 → 拒绝（CNT-S-010）
            await Assert.ThrowsAsync<ArgumentException>(() => run.AdjustCountAsync(1, "", "op"));

            var adjustment = await run.AdjustCountAsync(2, "补记两件漏检", "李工");
            Assert.Equal(0, adjustment.Before);
            Assert.Equal(2, adjustment.After);

            await run.DisposeAsync();

            // Corrected 事件已落库（等待后台写队列）
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            List<CountingEventEntity>? events = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                events = await ctx.Records.ListCountingEventsAsync();
                if (events.Count > 0)
                {
                    break;
                }
                await Task.Delay(100);
            }
            Assert.NotNull(events);
            Assert.Single(events!);
            Assert.Equal("Corrected", events![0].EventType);
            Assert.Equal(2, events[0].Delta);
        }
        finally
        {
            Directory.Delete(imagesDir, recursive: true);
        }
    }
}
