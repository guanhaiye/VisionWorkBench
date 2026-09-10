using System.Text.Json;
using System.Collections.Concurrent;
using VisionWorkbench.Application;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class SopProductResultReplayerTests
{
    [Fact]
    public async Task Pending_Product_Is_Replayed_After_Restart_And_Concurrent_Scans_Are_Idempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var publisher = new ReplayPublisher();
        await SeedPendingRunAsync(factory, 77, "old-cycle");

        try
        {
            await using (var first = new SopProductResultReplayer(new SopRunRepository(factory), publisher))
            await using (var second = new SopProductResultReplayer(new SopRunRepository(factory), publisher))
            {
                await Task.WhenAll(first.ReplayPendingOnceAsync(), second.ReplayPendingOnceAsync());
            }

            Assert.Single(publisher.Products);
            Assert.Equal("sop-product:77:final", publisher.Products[0].ResultId);

            await using (var restarted = new SopProductResultReplayer(new SopRunRepository(factory), publisher))
            {
                Assert.Equal(0, (await restarted.ReplayPendingOnceAsync()).SucceededCount);
            }

            await using var db = factory.CreateDbContext();
            var run = Assert.Single(db.SopRuns);
            Assert.NotNull(run.FinalPublishedAtUtc);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Failed_Replay_Remains_Pending_And_Succeeds_On_Next_Scan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-replay-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var publisher = new ReplayPublisher { FailuresRemaining = 1 };
        await SeedPendingRunAsync(factory, 88, "retry-cycle");

        try
        {
            await using var replayer = new SopProductResultReplayer(new SopRunRepository(factory), publisher);
            Assert.Equal(0, (await replayer.ReplayPendingOnceAsync()).SucceededCount);
            await using (var db = factory.CreateDbContext())
            {
                Assert.Null(Assert.Single(db.SopRuns).FinalPublishedAtUtc);
            }

            Assert.Equal(1, (await replayer.ReplayPendingOnceAsync()).SucceededCount);
            Assert.Single(publisher.Products);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Background_Replay_Uses_One_Two_Four_And_Capped_Backoff_And_Dispose_Cancels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-sop-replay-backoff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = VisionDbContextFactory.Create(Path.Combine(root, "test.db"));
        var publisher = new ReplayPublisher { FailuresRemaining = int.MaxValue };
        await SeedPendingRunAsync(factory, 99, "backoff-cycle");
        var delays = new ConcurrentQueue<TimeSpan>();
        var releases = new SemaphoreSlim(0);
        var observed = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var delayCalls = 0;

        async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            delays.Enqueue(delay);
            var call = Interlocked.Increment(ref delayCalls) - 1;
            if (call < observed.Length)
            {
                observed[call].TrySetResult(true);
            }
            await releases.WaitAsync(cancellationToken);
        }

        var replayer = new SopProductResultReplayer(
            new SopRunRepository(factory),
            publisher,
            delayAsync: DelayAsync,
            maxBackoff: TimeSpan.FromSeconds(4));
        try
        {
            replayer.Start();
            for (var index = 0; index < observed.Length; index++)
            {
                await observed[index].Task.WaitAsync(TimeSpan.FromSeconds(3));
                releases.Release();
            }

            Assert.Equal(
                new[] { 1, 2, 4, 4 },
                delays.Take(4).Select(delay => (int)delay.TotalSeconds).ToArray());
        }
        finally
        {
            await replayer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            releases.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task SeedPendingRunAsync(VisionDbContextFactory factory, long id, string cycleId)
    {
        var decision = new DecisionResult { Status = DecisionStatus.Ng };
        var envelope = new ProductResultEnvelope
        {
            ResultId = $"sop-product:{id}:final",
            ProjectId = "default",
            StationCode = "ST-REPLAY",
            TaskId = 9000,
            SopRunId = id,
            CycleId = cycleId,
            Decision = decision,
            WorkflowResultJson = "{}",
        };
        await using var db = factory.CreateDbContext();
        db.SopRuns.Add(new SopRunEntity
        {
            Id = id,
            SopDefinitionId = "test:replay",
            SopVersion = 1,
            DefinitionHash = "hash",
            DefinitionSnapshotJson = "{}",
            ProjectId = "default",
            StationCode = "ST-REPLAY",
            TaskId = 9000,
            CycleId = cycleId,
            Status = "NgTimeout",
            FinalStatus = "ng",
            FinalDecisionJson = JsonSerializer.Serialize(decision),
            FinalResultJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            FinalizedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class ReplayPublisher : IResultPublisher
    {
        public List<ProductResultEnvelope> Products { get; } = [];
        public int FailuresRemaining { get; set; }

        public Task PublishAsync(ResultEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishProductAsync(ProductResultEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("模拟发布失败");
            }
            lock (Products)
            {
                Products.Add(envelope);
            }
            return Task.CompletedTask;
        }
    }
}
