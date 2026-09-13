using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Cameras.Files;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace VisionWorkbench.Tests;

/// <summary>
/// 流水计数端到端（CNT-D / CNT-L 视频级用例）：
/// VideoFileProvider → sample-flow-counter Worker → 计数事件 → SQLite 落库 → 重放恢复。
/// 真值见 samples/manifest.json（conveyor_forward=12 / reverse=12 / mixed=4f+2r+1 折返不计）。
/// </summary>
public sealed class FlowCountingEndToEndTests(ITestOutputHelper output)
{
    private const string FlowPlugin = "com.vision.sample-flow-counter";

    private sealed class FlowContext : IAsyncDisposable
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

    private static string SampleVideo(string name)
    {
        var path = Path.Combine(TestPaths.Root, "samples", "conveyor", name);
        Assert.True(File.Exists(path), $"样本视频缺失: {path}");
        return path;
    }

    private static async Task<FlowContext> CreateContextAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"vw-flow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var factory = VisionDbContextFactory.Create(Path.Combine(dataDir, "test.db"));
        return new FlowContext
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

    private static Recipe FlowRecipe(CountingMode mode, string videoPath) => new()
    {
        Name = "flow-e2e",
        CameraProviderId = "video-file",
        CameraDeviceId = videoPath,
        PluginId = FlowPlugin,
        CountingMode = mode,
        CountingLine = new CountingLineConfig
        {
            A = new NormalizedPoint { X = 2.0 / 3.0, Y = 0.1 },
            B = new NormalizedPoint { X = 2.0 / 3.0, Y = 0.9 },
        },
        Rules = [], // 流水模式零规则（无 OK/NG 判定）
    };

    private sealed record RunOutcome(
        CounterState State,
        IReadOnlyList<CountingEvent> Events,
        IReadOnlyList<string> Faults);

    /// <summary>跑完整视频：video-file → flow-counter → 事件累计，返回最终状态与全部事件。</summary>
    private async Task<RunOutcome> RunVideoAsync(FlowContext ctx, Recipe recipe, string videoPath)
    {
        var camera = await new VideoFileProvider().CreateSessionAsync(
            new CameraDescriptor
            {
                ProviderId = "video-file",
                DeviceId = videoPath,
                DisplayName = videoPath,
            }, CancellationToken.None);
        await camera.OpenAsync(new CameraOpenOptions { FrameIntervalMs = 10, Loop = false }, CancellationToken.None);

        var task = await ctx.Tasks.SaveAsync(new TaskEntity
        {
            Name = $"flow-{Guid.NewGuid():N}",
            CameraProviderId = "video-file",
            CameraDeviceId = videoPath,
            PluginId = FlowPlugin,
        });
        var algorithm = await ctx.Manager.CreateSessionAsync(FlowPlugin, CancellationToken.None);
        var batch = await ctx.Batches.StartAsync(task.Id, 0);
        var run = new DetectionRunService(ctx.Records, ctx.TempImages,
            null, $"task-{task.Id}");

        var events = new List<CountingEvent>();
        var faults = new List<string>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.RecordCompleted += (_, e) => { lock (events) { events.AddRange(e.Output.CountingEvents); } };
        run.Faulted += (_, e) => { lock (faults) { faults.Add($"[{e.Source}] {e.Code}: {e.Message}"); } };
        run.SourceCompleted += (_, _) => finished.TrySetResult();

        try
        {
            await run.StartAsync(recipe, task.Id, camera, algorithm, batch.Id,
                FrameRoutingStrategy.Bounded); // 视频逐帧不漏（FRM-003）
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(90));
            await run.StopAsync();
            await ctx.Batches.EndAsync(batch.Id, run.Counting.State.CurrentTotal);
            return new RunOutcome(run.Counting.State, events, faults);
        }
        finally
        {
            await run.DisposeAsync();
            await camera.DisposeAsync();
        }
    }

    private static CountingEvent ToDomainEvent(CountingEventEntity e) => new()
    {
        EventId = $"db-{e.Id}",
        CounterId = e.CounterId,
        TrackId = e.TrackId,
        ClassId = e.ClassId,
        Type = Enum.TryParse<CountingEventType>(e.EventType, out var type) ? type : CountingEventType.Appeared,
        Direction = e.Direction,
        Delta = e.Delta,
        Confidence = e.Confidence,
        FrameSequence = e.FrameSequence,
        OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc), TimeSpan.Zero),
    };

    // ---- CNT-L-001/005 跨线正向 ----

    [Fact]
    public async Task Forward_Video_Line_Mode_Counts_12_Forward_With_TrackIds()
    {
        if (!TestPaths.VenvExists)
        {
            return; // 环境无 Python 时优雅跳过
        }
        var video = SampleVideo("conveyor_forward.avi");
        await using var ctx = await CreateContextAsync();
        var outcome = await RunVideoAsync(ctx, FlowRecipe(CountingMode.LineCrossing, video), video);

        Assert.Empty(outcome.Faults);
        Assert.Equal(12, outcome.State.ForwardTotal);
        Assert.Equal(0, outcome.State.ReverseTotal);
        Assert.Equal(12, outcome.State.CurrentTotal);

        var crossed = outcome.Events.Where(e => e.Type == CountingEventType.CrossedLine).ToArray();
        Assert.Equal(12, crossed.Length);                              // CNT-L-001：12 条 CrossedLine
        Assert.All(crossed, e => Assert.Equal("forward", e.Direction));
        Assert.All(crossed, e => Assert.False(string.IsNullOrEmpty(e.TrackId))); // DAT：事件可追溯
        Assert.Equal(12, crossed.Select(e => e.TrackId).Distinct().Count());     // CNT-L-005：无重复

        // 落库可再查询 + 重放恢复一致（CNT-L-014）
        var stored = await ctx.Records.ListCountingEventsAsync();
        Assert.Equal(12, stored.Count(e => e.EventType == "CrossedLine"));
        var replay = new CountingService("default");
        replay.RestoreFrom(stored.Select(ToDomainEvent));
        Assert.Equal(12, replay.State.ForwardTotal);
        Assert.Equal(12, replay.State.CurrentTotal);
        output.WriteLine($"forward 线模式: total={outcome.State.CurrentTotal} events={stored.Count}");
    }

    // ---- CNT-L-002 跨线反向 ----

    [Fact]
    public async Task Reverse_Video_Line_Mode_Counts_12_Reverse()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }
        var video = SampleVideo("conveyor_reverse.avi");
        await using var ctx = await CreateContextAsync();
        var outcome = await RunVideoAsync(ctx, FlowRecipe(CountingMode.LineCrossing, video), video);

        Assert.Empty(outcome.Faults);
        Assert.Equal(0, outcome.State.ForwardTotal);
        Assert.Equal(12, outcome.State.ReverseTotal); // CNT-L-002
        Assert.Equal(12, outcome.State.CurrentTotal);
    }

    // ---- CNT-D-001/008 动态去重累计 ----

    [Fact]
    public async Task Forward_Video_Unique_Mode_12_Appeared_No_Duplicate_TrackIds()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }
        var video = SampleVideo("conveyor_forward.avi");
        await using var ctx = await CreateContextAsync();
        var outcome = await RunVideoAsync(ctx, FlowRecipe(CountingMode.UniqueTracking, video), video);

        Assert.Empty(outcome.Faults);
        var appeared = outcome.Events.Where(e => e.Type == CountingEventType.Appeared).ToArray();
        Assert.Equal(12, appeared.Length);                                        // CNT-D-001
        Assert.Equal(12, appeared.Select(e => e.TrackId).Distinct().Count());     // CNT-D-008：无重复累计
        Assert.Equal(12, outcome.State.CurrentTotal);
    }

    // ---- CNT-L-009/004 混合方向 + 折返不计（视频级） ----

    [Fact]
    public async Task Mixed_Video_Separates_Directions_And_Skips_Foldback()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }
        var video = SampleVideo("conveyor_mixed.avi");
        await using var ctx = await CreateContextAsync();
        var outcome = await RunVideoAsync(ctx, FlowRecipe(CountingMode.LineCrossing, video), video);

        Assert.Empty(outcome.Faults);
        Assert.Equal(4, outcome.State.ForwardTotal);
        Assert.Equal(2, outcome.State.ReverseTotal);
        Assert.Equal(6, outcome.State.CurrentTotal); // 折返 1 个目标未越过确认区不计（CNT-L-004）
    }

    // ---- CNT-L-012 counter_command reset 清 worker 跟踪记忆（协议直连） ----

    [Fact]
    public async Task CounterCommand_Reset_Clears_Worker_Memory_And_Recounts()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }
        var video = SampleVideo("conveyor_forward.avi");

        // 抽前 30 帧（仅目标 0 入画并确认；目标 1 于第 55 帧后入场）
        var framesDir = Path.Combine(Path.GetTempPath(), $"vw-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(framesDir);
        try
        {
            var frames = new List<string>();
            using (var capture = new VideoCapture(video))
            using (var mat = new Mat())
            {
                for (var i = 0; i < 30; i++)
                {
                    if (!capture.Read(mat) || mat.Empty())
                    {
                        break;
                    }
                    var path = Path.Combine(framesDir, $"f{i:000}.png");
                    Cv2.ImWrite(path, mat);
                    frames.Add(path);
                }
            }
            Assert.True(frames.Count >= 15, "视频抽帧不足");

            await using var ctx = await CreateContextAsync();
            var algorithm = await ctx.Manager.CreateSessionAsync(FlowPlugin, CancellationToken.None);
            await algorithm.InitializeAsync(new AlgorithmInitialization
            {
                Settings = JsonDocument.Parse(
                    """{"countingMode":"unique","minAreaPx":500}""").RootElement.Clone(),
            }, CancellationToken.None);
            await algorithm.StartAsync(new AlgorithmStartOptions { Mode = "stream" }, CancellationToken.None);

            async Task<int> SubmitAllAsync()
            {
                var appeared = 0;
                for (var i = 0; i < frames.Count; i++)
                {
                    var result = await algorithm.SubmitAsync(new AlgorithmInput
                    {
                        InputId = $"in-{i}",
                        ImagePath = frames[i],
                        FrameSequence = i,
                    }, CancellationToken.None);
                    appeared += result.CountingEvents.Count(
                        e => e.Type == CountingEventType.Appeared);
                }
                return appeared;
            }

            Assert.Equal(1, await SubmitAllAsync()); // 目标 0 确认 → 1 条 Appeared

            // 会话内清零（CNT-L-012）：worker 清除轨迹与去重记忆
            await algorithm.SendCommandAsync(
                MessageType.CounterCommand, new { command = "reset" }, CancellationToken.None);

            Assert.Equal(1, await SubmitAllAsync()); // 同目标重现 → 重新累计
        }
        finally
        {
            Directory.Delete(framesDir, recursive: true);
        }
    }

    // ---- CFG-004 配套：流水模式零规则可保存 + 模式/检测线持久化往返 ----

    [Fact]
    public async Task RecipeService_RoundTrips_CountingMode_And_Line()
    {
        await using var ctx = await CreateContextAsync();
        var service = new RecipeService(ctx.Tasks);

        // 快照模式零规则仍被拒绝（原校验不变）
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new Recipe
        {
            Name = "snap-zero-rules",
            CameraProviderId = "video-file",
            CameraDeviceId = "x.avi",
            PluginId = FlowPlugin,
            Rules = [],
        }));

        var videoPath = Path.Combine(ctx.DataDir, "conveyor.avi");
        File.WriteAllBytes(videoPath, []);
        var saved = await service.SaveAsync(new Recipe
        {
            Name = "flow-roundtrip",
            CameraProviderId = "video-file",
            CameraDeviceId = videoPath,
            PluginId = FlowPlugin,
            CountingMode = CountingMode.LineCrossing,
            CountingLine = new CountingLineConfig
            {
                A = new NormalizedPoint { X = 0.1, Y = 0.2 },
                B = new NormalizedPoint { X = 0.3, Y = 0.8 },
                Hysteresis = 0.05,
            },
            Rules = [], // 流水模式允许零规则
        });
        var found = await service.FindAsync(saved.Id);
        Assert.NotNull(found);
        var recipe = found!.Value.Recipe;
        Assert.Equal(CountingMode.LineCrossing, recipe.CountingMode);
        Assert.NotNull(recipe.CountingLine);
        Assert.Equal(0.1, recipe.CountingLine!.A.X, 3);
        Assert.Equal(0.8, recipe.CountingLine.B.Y, 3);
        Assert.Equal(0.05, recipe.CountingLine.Hysteresis, 3);
        Assert.Empty(recipe.Rules);
    }

    [Fact]
    public async Task RecipeService_Saves_ContourAnalysis_Without_Rules()
    {
        await using var ctx = await CreateContextAsync();
        var service = new RecipeService(ctx.Tasks);
        var imageDirectory = Path.Combine(ctx.DataDir, "images");
        Directory.CreateDirectory(imageDirectory);

        var saved = await service.SaveAsync(new Recipe
        {
            Name = "contour-analysis",
            CameraProviderId = "image-folder",
            CameraDeviceId = imageDirectory,
            PluginId = FlowPlugin,
            TaskType = InspectionTaskType.ContourAnalysis,
            Rules = [],
        });

        var found = await service.FindAsync(saved.Id);
        Assert.NotNull(found);
        Assert.Equal(InspectionTaskType.ContourAnalysis, found!.Value.Recipe.TaskType);
        Assert.Empty(found.Value.Recipe.Rules);
    }
}
