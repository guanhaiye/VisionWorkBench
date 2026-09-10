using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace VisionWorkbench.Tests;

public sealed class DetectionRunSopTests(ITestOutputHelper output)
{
    private sealed class OutputLogger(ITestOutputHelper output) : ILogger<DetectionRunService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" | {exception}")}");
    }
    [Fact]
    public async Task Terminal_Product_Can_Start_A_New_Sop_Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-cycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var recordCompleted = new TaskCompletionSource<RecordCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new SopDefinition
        {
            Id = "test:cycle",
            Code = "CYCLE",
            Name = "周期测试",
            Steps =
            [
                new SopStep
                {
                    Id = "step-1",
                    Code = "STEP-1",
                    Name = "检测部件",
                    Order = 1,
                    MinimumStableFrames = 1,
                    TimeoutSeconds = 30,
                    Conditions =
                    [new SopCondition
                    {
                        Id = "condition-1",
                        Kind = SopConditionKind.ObjectPresent,
                        EventType = "object.present",
                        ClassId = "part",
                    }],
                },
            ],
        };
        var recipe = new Recipe
        {
            Name = "周期测试任务",
            StationCode = "ST-CYCLE",
            CameraProviderId = "fake",
            CameraDeviceId = "fake-camera",
            PluginId = "fake-plugin",
            Sop = new SopBinding
            {
                DefinitionId = definition.Id,
                Version = definition.Version,
                Definition = definition,
            },
        };
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            NullLogger<DetectionRunService>.Instance,
            sopRuns: new SopRunRepository(factory));
        run.RecordCompleted += (_, args) => recordCompleted.TrySetResult(args);

        try
        {
            await run.StartAsync(recipe, 1001, camera, algorithm, startProcessingLoop: true);
            var firstRunId = run.SopRunId;
            var firstCycleId = run.SopCycleId;
            Assert.NotNull(firstRunId);
            Assert.NotNull(firstCycleId);

            camera.Emit(new VideoFrame(1, DateTimeOffset.UtcNow, 1, 1, [0, 0, 0]));
            var first = await recordCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SopRunStatus.CompletedOk, first.Sop?.Status);

            var nextRuns = await Task.WhenAll(
                run.StartNextProductAsync(),
                run.StartNextProductAsync());

            Assert.All(nextRuns, next => Assert.Equal(SopRunStatus.WaitingForStep, next?.Status));
            Assert.NotEqual(firstRunId, run.SopRunId);
            Assert.NotEqual(firstCycleId, run.SopCycleId);
            await run.StopAsync();
            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(2, db.SopRuns.Count());
            }
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Runtime_Clock_Times_Out_Without_A_New_Frame()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var timedOut = new TaskCompletionSource<SopSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new SopDefinition
        {
            Id = "test:timeout",
            Code = "TIMEOUT",
            Name = "超时测试",
            Steps =
            [new SopStep
            {
                Id = "step-timeout",
                Code = "STEP-TIMEOUT",
                Name = "等待部件",
                Order = 1,
                MinimumStableFrames = 1,
                TimeoutSeconds = 0.15,
                Conditions =
                [new SopCondition
                {
                    Id = "condition-timeout",
                    Kind = SopConditionKind.ObjectPresent,
                    EventType = "object.present",
                    ClassId = "part",
                }],
            }],
        };
        var recipe = new Recipe
        {
            Name = "超时测试任务",
            StationCode = "ST-TIMEOUT",
            CameraProviderId = "fake",
            CameraDeviceId = "fake-camera",
            PluginId = "fake-plugin",
            Sop = new SopBinding { DefinitionId = definition.Id, Definition = definition },
        };
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            NullLogger<DetectionRunService>.Instance,
            sopRuns: new SopRunRepository(factory));
        run.SopChanged += snapshot =>
        {
            if (snapshot.Status == SopRunStatus.NgTimeout)
            {
                timedOut.TrySetResult(snapshot);
            }
        };

        try
        {
            await run.StartAsync(recipe, 1002, camera, algorithm, startProcessingLoop: true);
            var snapshot = await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(SopRunStatus.NgTimeout, snapshot.Status);
            Assert.Contains("超时", snapshot.FailureReason);
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Pause_Does_Not_Consume_Sop_Timeout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-pause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var timedOut = new TaskCompletionSource<SopSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new SopDefinition
        {
            Id = "test:pause",
            Code = "PAUSE",
            Name = "暂停计时测试",
            Steps = [new SopStep
            {
                Id = "step-pause",
                Code = "STEP-PAUSE",
                Name = "等待部件",
                Order = 1,
                MinimumStableFrames = 1,
                TimeoutSeconds = 0.2,
                Conditions = [new SopCondition
                {
                    Id = "condition-pause",
                    Kind = SopConditionKind.ObjectPresent,
                    EventType = "object.present",
                    ClassId = "part",
                }],
            }],
        };
        var recipe = CreateRecipe(definition, "ST-PAUSE");
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            NullLogger<DetectionRunService>.Instance,
            sopRuns: new SopRunRepository(factory));
        run.SopChanged += snapshot =>
        {
            if (snapshot.Status == SopRunStatus.NgTimeout)
                timedOut.TrySetResult(snapshot);
        };

        try
        {
            await run.StartAsync(recipe, 1003, camera, algorithm, startProcessingLoop: true);
            await Task.Delay(80);
            await run.PauseAsync();
            await Task.Delay(350);
            Assert.NotEqual(SopRunStatus.NgTimeout, run.Sop?.Status);

            await run.ResumeAsync();
            var snapshot = await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(SopRunStatus.NgTimeout, snapshot.Status);
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Timeout_Persists_Product_Result_And_Publishes_Once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-product-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var publisher = new RecordingPublisher();
        var timedOut = new TaskCompletionSource<SopSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new SopDefinition
        {
            Id = "test:product-result",
            Code = "PRODUCT-RESULT",
            Name = "产品结果测试",
            Steps = [new SopStep
            {
                Id = "step-product-result",
                Code = "STEP-PRODUCT-RESULT",
                Name = "等待部件",
                Order = 1,
                MinimumStableFrames = 1,
                TimeoutSeconds = 0.15,
                Conditions = [new SopCondition
                {
                    Id = "condition-product-result",
                    Kind = SopConditionKind.ObjectPresent,
                    EventType = "object.present",
                    ClassId = "part",
                }],
            }],
        };
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            new OutputLogger(output),
            publisher: publisher,
            sopRuns: new SopRunRepository(factory));
        run.SopChanged += snapshot =>
        {
            if (snapshot.Status == SopRunStatus.NgTimeout)
                timedOut.TrySetResult(snapshot);
        };

        try
        {
            await run.StartAsync(CreateRecipe(definition, "ST-PRODUCT-RESULT"), 1004, camera, algorithm, startProcessingLoop: true);
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await publisher.ProductPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // 终态时钟与随后到达的帧竞争：帧可以产生普通记录，但不得再次发布产品终态。
            camera.Emit(new VideoFrame(2, DateTimeOffset.UtcNow, 1, 1, [0, 0, 0]));
            await run.StopAsync();

            Assert.Single(publisher.Products);
            Assert.Equal(DecisionStatus.Ng, publisher.Products[0].Decision.Status);
            await using var db = factory.CreateDbContext();
            var stored = Assert.Single(db.SopRuns);
            Assert.Equal("ng", stored.FinalStatus);
            Assert.NotNull(stored.FinalDecisionJson);
            Assert.NotNull(stored.FinalResultJson);
            using (var decisionJson = JsonDocument.Parse(stored.FinalDecisionJson!))
            {
                Assert.Equal((int)DecisionStatus.Ng, decisionJson.RootElement.GetProperty("status").GetInt32());
            }
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Frame_Record_FinalResult_And_Publisher_Use_The_Same_Decision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-frame-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        await using (var seedDb = factory.CreateDbContext())
        {
            seedDb.Tasks.Add(new TaskEntity
            {
                Id = 1005,
                StationCode = "ST-FRAME-RESULT",
                Name = "帧结果测试任务",
                CameraProviderId = "fake",
                CameraDeviceId = "fake-camera",
                PluginId = "fake-plugin",
            });
            await seedDb.SaveChangesAsync();
        }
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var publisher = new RecordingPublisher();
        var completed = new TaskCompletionSource<RecordCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new SopDefinition
        {
            Id = "test:frame-result",
            Code = "FRAME-RESULT",
            Name = "帧结果测试",
            Steps = [new SopStep
            {
                Id = "step-frame-result",
                Code = "STEP-FRAME-RESULT",
                Name = "检测部件",
                Order = 1,
                MinimumStableFrames = 1,
                Conditions = [new SopCondition
                {
                    Id = "condition-frame-result",
                    Kind = SopConditionKind.ObjectPresent,
                    EventType = "object.present",
                    ClassId = "part",
                }],
            }],
        };
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            new OutputLogger(output),
            publisher: publisher,
            sopRuns: new SopRunRepository(factory));
        run.RecordCompleted += (_, args) => completed.TrySetResult(args);

        try
        {
            await run.StartAsync(CreateRecipe(definition, "ST-FRAME-RESULT"), 1005, camera, algorithm, startProcessingLoop: true);
            camera.Emit(new VideoFrame(1, DateTimeOffset.UtcNow, 1, 1, [0, 0, 0]));
            var args = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.StopAsync();
            await publisher.FramePublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var published = Assert.Single(publisher.Frames);
            var product = Assert.Single(publisher.Products);
            var storedDecision = JsonSerializer.Deserialize<ResultEnvelope>(
                args.Record.FinalResultJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Decision;
            Assert.Equal("ok", args.Record.Status);
            Assert.Equal(DecisionStatus.Ok, args.Decision.Status);
            Assert.Equal(args.Decision.Status, storedDecision?.Status);
            Assert.Equal(args.Decision.Status, published.Decision?.Status);
            Assert.Equal(args.Decision.Status, product.Decision.Status);
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Frame_SopCompletion_With_VisualNg_Publishes_One_ProductNg()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-frame-ng-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        await using (var seedDb = factory.CreateDbContext())
        {
            seedDb.Tasks.Add(new TaskEntity
            {
                Id = 1006,
                StationCode = "ST-FRAME-NG",
                Name = "帧 NG 测试任务",
                CameraProviderId = "fake",
                CameraDeviceId = "fake-camera",
                PluginId = "fake-plugin",
            });
            await seedDb.SaveChangesAsync();
        }

        var definition = new SopDefinition
        {
            Id = "test:frame-ng",
            Code = "FRAME-NG",
            Name = "帧 NG 测试",
            Steps = [new SopStep
            {
                Id = "step-frame-ng",
                Code = "STEP-FRAME-NG",
                Name = "检测部件",
                Order = 1,
                MinimumStableFrames = 1,
                Conditions = [new SopCondition
                {
                    Id = "condition-frame-ng",
                    Kind = SopConditionKind.ObjectPresent,
                    EventType = "object.present",
                    ClassId = "part",
                }],
            }],
        };
        var recipe = CreateRecipe(definition, "ST-FRAME-NG") with
        {
            Rules = [new InspectionRule
            {
                RuleId = "need-two-parts",
                Kind = RuleKind.CountEquals,
                ExpectedCount = 2,
            }],
        };
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var publisher = new RecordingPublisher();
        var completed = new TaskCompletionSource<RecordCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            NullLogger<DetectionRunService>.Instance,
            publisher: publisher,
            sopRuns: new SopRunRepository(factory));
        run.RecordCompleted += (_, args) => completed.TrySetResult(args);

        try
        {
            await run.StartAsync(recipe, 1006, camera, algorithm, startProcessingLoop: true);
            camera.Emit(new VideoFrame(1, DateTimeOffset.UtcNow, 1, 1, [0, 0, 0]));
            var args = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await run.StopAsync();
            await publisher.ProductPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var product = Assert.Single(publisher.Products);
            Assert.Equal(DecisionStatus.Ng, args.Decision.Status);
            Assert.Equal("ng", args.Record.Status);
            Assert.Equal(DecisionStatus.Ng, product.Decision.Status);
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Failed_Product_Publish_Is_Replayed_When_Next_Product_Starts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-product-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var camera = new FakeCamera();
        var algorithm = new FakeAlgorithm();
        var publisher = new RecordingPublisher
        {
            FailProductPublishes = 2,
            ProductPublishEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ProductPublishGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var replayer = new SopProductResultReplayer(
            new SopRunRepository(factory),
            publisher,
            pollInterval: TimeSpan.FromHours(1));
        replayer.Start();
        var timedOut = new TaskCompletionSource<SopSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new SopDefinition
        {
            Id = "test:product-retry",
            Code = "PRODUCT-RETRY",
            Name = "产品发布重试",
            Steps = [new SopStep
            {
                Id = "step-product-retry",
                Code = "STEP-PRODUCT-RETRY",
                Name = "等待部件",
                Order = 1,
                MinimumStableFrames = 1,
                TimeoutSeconds = 0.12,
                Conditions = [new SopCondition
                {
                    Id = "condition-product-retry",
                    Kind = SopConditionKind.ObjectPresent,
                    EventType = "object.present",
                    ClassId = "part",
                }],
            }],
        };
        var run = new DetectionRunService(
            new RecordRepository(factory),
            new TempImageStore(Path.Combine(root, "temp-images")).Initialize(),
            NullLogger<DetectionRunService>.Instance,
            publisher: publisher,
            sopRuns: new SopRunRepository(factory),
            pendingReplayTrigger: replayer);
        run.SopChanged += snapshot =>
        {
            if (snapshot.Status == SopRunStatus.NgTimeout)
                timedOut.TrySetResult(snapshot);
        };

        try
        {
            await run.StartAsync(CreateRecipe(definition, "ST-PRODUCT-RETRY"), 1007, camera, algorithm, startProcessingLoop: true);
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(publisher.Products);

            var failedCycleId = run.SopCycleId;
            Assert.NotNull(failedCycleId);
            await WaitForPendingRunAsync(factory, failedCycleId);
            var switchTask = run.StartNextProductAsync();
            await switchTask.WaitAsync(TimeSpan.FromSeconds(1));
            await publisher.ProductPublishEntered!.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(switchTask.IsCompletedSuccessfully);
            publisher.ProductPublishGate!.TrySetResult(true);
            var product = await publisher.ProductPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains(publisher.Products, item => item.CycleId == failedCycleId);
            Assert.NotEqual(run.SopCycleId, product.CycleId);
            Assert.Equal(failedCycleId, product.CycleId);
            Assert.Equal(DecisionStatus.Ng, product.Decision.Status);
        }
        finally
        {
            await run.DisposeAsync();
            camera.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task WaitForPendingRunAsync(VisionDbContextFactory factory, string cycleId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var db = factory.CreateDbContext();
            if (db.SopRuns.Any(run => run.CycleId == cycleId
                && run.FinalizedAtUtc.HasValue
                && run.FinalPublishedAtUtc == null
                && !string.IsNullOrWhiteSpace(run.FinalResultJson)))
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"未等到 SOP 周期进入待重放状态: {cycleId}");
    }

    private static Recipe CreateRecipe(SopDefinition definition, string stationCode) => new()
    {
        Name = definition.Name,
        StationCode = stationCode,
        CameraProviderId = "fake",
        CameraDeviceId = "fake-camera",
        PluginId = "fake-plugin",
        Sop = new SopBinding { DefinitionId = definition.Id, Version = definition.Version, Definition = definition },
    };

    private sealed class RecordingPublisher : IResultPublisher
    {
        public List<ResultEnvelope> Frames { get; } = [];
        public List<ProductResultEnvelope> Products { get; } = [];
        public int FailProductPublishes { get; set; }
        public TaskCompletionSource<bool>? ProductPublishEntered { get; init; }
        public TaskCompletionSource<bool>? ProductPublishGate { get; init; }
        public TaskCompletionSource<ResultEnvelope> FramePublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProductResultEnvelope> ProductPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(ResultEnvelope envelope, CancellationToken cancellationToken = default)
        {
            lock (Frames) Frames.Add(envelope);
            FramePublished.TrySetResult(envelope);
            return Task.CompletedTask;
        }

        public async Task PublishProductAsync(ProductResultEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (FailProductPublishes > 0)
            {
                FailProductPublishes--;
                throw new InvalidOperationException("模拟产品结果发布失败");
            }
            ProductPublishEntered?.TrySetResult(true);
            if (ProductPublishGate is not null)
            {
                await ProductPublishGate.Task.WaitAsync(cancellationToken);
            }
            lock (Products) Products.Add(envelope);
            ProductPublished.TrySetResult(envelope);
            return;
        }
    }

    #pragma warning disable CS0067
    private sealed class FakeCamera : ICameraSession
    {
        public CameraDescriptor Descriptor { get; } = new()
        {
            ProviderId = "fake",
            DeviceId = "fake-camera",
            DisplayName = "Fake Camera",
        };
        public CameraSessionState State { get; private set; } = CameraSessionState.Idle;
        public CameraCapabilities Capabilities { get; } = new();
        public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
        public event EventHandler<CameraFaultedEventArgs>? Faulted;
        public event EventHandler? Completed;
        public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) { State = CameraSessionState.Streaming; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken) { State = CameraSessionState.Paused; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken) { State = CameraSessionState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { State = CameraSessionState.Idle; return Task.CompletedTask; }
        public Task ApplyParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Emit(VideoFrame frame) => FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(frame));
        public void Dispose() { State = CameraSessionState.Closed; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeAlgorithm : IAlgorithmSession
    {
        public string SessionId { get; } = "fake-session";
        public AlgorithmSessionState State { get; private set; } = AlgorithmSessionState.Ready;
        public event EventHandler<AlgorithmOutputEventArgs>? OutputReceived;
        public event EventHandler<AlgorithmEventEventArgs>? EventReceived;
        public event EventHandler<AlgorithmFaultedEventArgs>? Faulted;
        public Task InitializeAsync(AlgorithmInitialization initialization, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(AlgorithmStartOptions options, CancellationToken cancellationToken) { State = AlgorithmSessionState.Running; return Task.CompletedTask; }
        public Task<AlgorithmOutput> SubmitAsync(AlgorithmInput input, CancellationToken cancellationToken) => Task.FromResult(new AlgorithmOutput
        {
            OutputId = $"output-{input.FrameSequence}",
            InputId = input.InputId,
            Sequence = input.FrameSequence,
            Timestamp = input.CapturedAt,
            Detections =
            [new DetectionResult
            {
                ClassId = "part",
                ClassName = "部件",
                Confidence = 0.99,
                Box = new NormalizedRect { X = 0, Y = 0, Width = 1, Height = 1 },
            }],
        });
        public Task SendCommandAsync(string messageType, object payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { State = AlgorithmSessionState.Stopped; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { State = AlgorithmSessionState.Stopped; return ValueTask.CompletedTask; }
    }
    #pragma warning restore CS0067
}
