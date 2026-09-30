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

    private sealed class FakeSession(Action initialized) : IAlgorithmSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
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
        public ValueTask DisposeAsync() { State = AlgorithmSessionState.Stopped; return ValueTask.CompletedTask; }
    }
}
