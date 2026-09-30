using System.Text.Json;
using VisionWorkbench.Algorithms;
using VisionWorkbench.App;
using VisionWorkbench.Contracts.Results;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class AlgorithmSessionCacheTests
{
    [Fact]
    public async Task Cache_ReusesWithinTaskButIsolatesDifferentTasksAndInitializesOnce()
    {
        var created = 0;
        var initialized = 0;
        var cache = new AlgorithmSessionCache((_, _) =>
        {
            Interlocked.Increment(ref created);
            return Task.FromResult<IAlgorithmSession>(new FakeSession(() => Interlocked.Increment(ref initialized)));
        });
        var init = new AlgorithmInitialization { ExecutionProvider = "cpu" };

        var sameTask = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrInitializeAsync(11, "plugin.test", init)));
        var otherTask = await cache.GetOrInitializeAsync(12, "plugin.test", init);

        Assert.All(sameTask, session => Assert.Same(sameTask[0], session));
        Assert.NotSame(sameTask[0], otherTask);
        Assert.Equal(2, created);
        Assert.Equal(2, initialized);
    }

    [Fact]
    public async Task SessionPool_UsesIndependentSlotsAndReusesInitializedWorkerModels()
    {
        var created = 0;
        var initialized = 0;
        var cache = new AlgorithmSessionCache((_, _) =>
        {
            Interlocked.Increment(ref created);
            return Task.FromResult<IAlgorithmSession>(new FakeSession(() => Interlocked.Increment(ref initialized)));
        });
        var init = new AlgorithmInitialization { ExecutionProvider = "cpu" };

        var first = await cache.AcquireSessionUseAsync(31, "plugin.test", init, 3);
        var second = await cache.AcquireSessionUseAsync(31, "plugin.test", init, 3);
        Assert.NotSame(first.Session, second.Session);
        Assert.Equal(2, created);
        Assert.Equal(2, initialized);

        await first.Session.StartAsync(new AlgorithmStartOptions { Mode = "stream" }, CancellationToken.None);
        await second.Session.StartAsync(new AlgorithmStartOptions { Mode = "stream" }, CancellationToken.None);
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(AlgorithmSessionState.Ready, first.Session.State);
        Assert.Equal(AlgorithmSessionState.Ready, second.Session.State);

        var reused = await cache.AcquireSessionUseAsync(31, "plugin.test", init, 3);
        Assert.Contains(reused.Session, new[] { first.Session, second.Session });
        await reused.DisposeAsync();
        Assert.Equal(2, created);
        Assert.Equal(2, initialized);
        await cache.DisposeAsync();
    }

    [Fact]
    public async Task SessionPool_RebuildsFaultedSlotAndIsolatesTasks()
    {
        var created = 0;
        var disposed = 0;
        var cache = new AlgorithmSessionCache((_, _) =>
        {
            Interlocked.Increment(ref created);
            return Task.FromResult<IAlgorithmSession>(new FakeSession(() => { }, () => Interlocked.Increment(ref disposed)));
        });
        var init = new AlgorithmInitialization { ExecutionProvider = "cpu" };
        var first = await cache.AcquireSessionUseAsync(41, "plugin.test", init, 2);
        var firstSession = (FakeSession)first.Session;
        firstSession.MarkFaulted();
        await first.DisposeAsync();
        Assert.Equal(1, disposed);

        var rebuilt = await cache.AcquireSessionUseAsync(41, "plugin.test", init, 2);
        Assert.NotSame(firstSession, rebuilt.Session);
        var otherTask = await cache.AcquireSessionUseAsync(42, "plugin.test", init, 2);
        Assert.NotSame(rebuilt.Session, otherTask.Session);
        Assert.Equal(3, created);
        await rebuilt.DisposeAsync();
        await otherTask.DisposeAsync();
        await cache.DisposeAsync();
        Assert.Equal(3, disposed);
    }
    private sealed class FakeSession(Action initialized, Action? disposed = null) : IAlgorithmSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public void MarkFaulted() => State = AlgorithmSessionState.Faulted;
        public AlgorithmSessionState State { get; private set; } = AlgorithmSessionState.Uninitialized;
        public event EventHandler<AlgorithmOutputEventArgs>? OutputReceived;
        public event EventHandler<AlgorithmEventEventArgs>? EventReceived;
        public event EventHandler<AlgorithmFaultedEventArgs>? Faulted;

        public Task InitializeAsync(AlgorithmInitialization initialization, CancellationToken cancellationToken)
        {
            initialized();
            State = AlgorithmSessionState.Ready;
            return Task.CompletedTask;
        }
        public Task StartAsync(AlgorithmStartOptions options, CancellationToken cancellationToken)
        { State = AlgorithmSessionState.Running; return Task.CompletedTask; }
        public Task<AlgorithmOutput> SubmitAsync(AlgorithmInput input, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SendCommandAsync(string messageType, object payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        { State = AlgorithmSessionState.Ready; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { State = AlgorithmSessionState.Stopped; disposed?.Invoke(); return ValueTask.CompletedTask; }
    }
}
