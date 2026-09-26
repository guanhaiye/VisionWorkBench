using VisionWorkbench.Algorithms;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Application;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class StationRunCoordinatorTests
{
    [Fact]
    public async Task StartAll_Reports_Disabled_Station_Without_Starting_Resources()
    {
        await using var coordinator = new StationRunCoordinator(null!, null!, null!);
        var results = await coordinator.StartAllAsync(
        [
            new StationStartRequest
            {
                Station = new StationEntity
                {
                    Id = 11,
                    StationCode = "ST-011",
                    Enabled = false,
                },
                ProjectId = "project-a",
                PhysicalDeviceKey = "usb:11",
                TaskId = 1,
                Recipe = null!,
                Camera = null!,
                Algorithm = null!,
            },
        ]);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Contains("未启用", result.Error);
        Assert.Empty(coordinator.RunningStationIds);
    }

    [Fact]
    public async Task Failed_Stop_Still_Disposes_Camera_And_Algorithm()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Camera.StopError = new InvalidOperationException("camera stop failed");
        await fixture.Coordinator.StartAsync(fixture.Request);

        var result = await fixture.Coordinator.StopAsync(fixture.Request.Station.Id);

        Assert.False(result.Succeeded);
        Assert.Contains("camera stop failed", result.Error);
        Assert.True(fixture.Camera.Disposed);
        Assert.True(fixture.Algorithm.Disposed);
        Assert.Empty(fixture.Coordinator.RunningStationIds);
    }

    [Fact]
    public async Task Failed_Startup_Disposes_All_Resources_And_Preserves_Original_Error()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = new InvalidOperationException("algorithm start failed");
        fixture.Algorithm.StartError = original;
        fixture.Camera.DisposeError = new IOException("camera dispose failed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.StartAsync(fixture.Request));

        Assert.Same(original, error);
        Assert.True(fixture.Camera.Disposed);
        Assert.True(fixture.Algorithm.Disposed);
        Assert.Empty(fixture.Coordinator.RunningStationIds);
    }

    [Fact]
    public async Task Device_Remains_Reserved_Until_Stop_Finishes()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Camera.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Coordinator.StartAsync(fixture.Request);
        var stopping = fixture.Coordinator.StopAsync(fixture.Request.Station.Id);
        await fixture.Camera.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.StartAsync(fixture.Request with
                {
                    Station = new StationEntity { Id = 2, StationCode = "ST-002", Enabled = true },
                }));
            Assert.Contains("占用", error.Message);
            Assert.Contains(fixture.Request.Station.Id, fixture.Coordinator.RunningStationIds);
        }
        finally
        {
            fixture.Camera.StopGate.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required StationRunCoordinator Coordinator { get; init; }
        public required StationStartRequest Request { get; init; }
        public required FakeCamera Camera { get; init; }
        public required FakeAlgorithm Algorithm { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"vw-station-cleanup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
            var task = await new TaskRepository(factory).SaveAsync(new TaskEntity
            {
                Name = "resource lifecycle",
                CameraProviderId = "fake",
                CameraDeviceId = "camera",
                PluginId = "fake",
            });
            var camera = new FakeCamera();
            var algorithm = new FakeAlgorithm();
            return new Fixture
            {
                Root = root,
                Camera = camera,
                Algorithm = algorithm,
                Coordinator = new StationRunCoordinator(new RecordRepository(factory),
                    new BatchRepository(factory), new TempImageStore(Path.Combine(root, "frames")).Initialize()),
                Request = new StationStartRequest
                {
                    Station = new StationEntity { Id = 1, StationCode = "ST-001", Enabled = true },
                    TaskId = task.Id,
                    ProjectId = "default",
                    PhysicalDeviceKey = "fake-camera",
                    Camera = camera,
                    Algorithm = algorithm,
                    Recipe = new Recipe
                    {
                        Name = "resource lifecycle",
                        CameraProviderId = "fake",
                        CameraDeviceId = "camera",
                        PluginId = "fake",
                    },
                },
            };
        }

        public async ValueTask DisposeAsync()
        {
            Camera.StopGate?.TrySetResult();
            await Coordinator.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

#pragma warning disable CS0067
    private sealed class FakeCamera : ICameraSession
    {
        public CameraDescriptor Descriptor { get; } = new()
        {
            ProviderId = "fake", DeviceId = "camera", DisplayName = "fake camera",
        };
        public CameraSessionState State => CameraSessionState.Streaming;
        public CameraCapabilities Capabilities { get; } = new();
        public Exception? StopError { get; set; }
        public Exception? DisposeError { get; set; }
        public TaskCompletionSource? StopGate { get; set; }
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
        public event EventHandler<CameraFaultedEventArgs>? Faulted;
        public event EventHandler? Completed;
        public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopEntered.TrySetResult();
            if (StopGate is not null) await StopGate.Task.WaitAsync(cancellationToken);
            if (StopError is not null) throw StopError;
        }
        public Task ApplyParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeError);
        }
    }

    private sealed class FakeAlgorithm : IAlgorithmSession
    {
        public string SessionId => "fake";
        public AlgorithmSessionState State { get; private set; } = AlgorithmSessionState.Ready;
        public Exception? StartError { get; set; }
        public bool Disposed { get; private set; }
        public event EventHandler<AlgorithmOutputEventArgs>? OutputReceived;
        public event EventHandler<AlgorithmEventEventArgs>? EventReceived;
        public event EventHandler<AlgorithmFaultedEventArgs>? Faulted;
        public Task InitializeAsync(AlgorithmInitialization initialization, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(AlgorithmStartOptions options, CancellationToken cancellationToken)
        {
            if (StartError is not null) return Task.FromException(StartError);
            State = AlgorithmSessionState.Running;
            return Task.CompletedTask;
        }
        public Task<AlgorithmOutput> SubmitAsync(AlgorithmInput input, CancellationToken cancellationToken) =>
            Task.FromResult(new AlgorithmOutput { OutputId = "fake", InputId = input.InputId });
        public Task SendCommandAsync(string messageType, object payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { State = AlgorithmSessionState.Stopped; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
#pragma warning restore CS0067
}
