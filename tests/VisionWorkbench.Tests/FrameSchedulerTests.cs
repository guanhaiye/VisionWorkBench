using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>帧调度器：LatestOnly 丢帧保新（FRM-001/002）、有界队列、有限源完成。</summary>
public sealed class FrameSchedulerTests
{
    private static VideoFrame Frame(long seq) => new(seq, DateTimeOffset.UtcNow, 8, 8, new byte[8 * 8 * 3]);

    [Fact]
    public async Task LatestOnly_Slow_Consumer_Keeps_Newest_And_Counts_Drops()
    {
        var scheduler = new FrameScheduler(FrameRoutingStrategy.LatestOnly);
        long previewSeen = 0;
        scheduler.PreviewReceived += (_, _) => Interlocked.Increment(ref previewSeen);

        // 生产快于消费：每帧预览都收到，推理只拿到最新
        for (var i = 1; i <= 5; i++)
        {
            scheduler.OnFrame(Frame(i));
        }
        var taken = await scheduler.TakeNextAsync(CancellationToken.None);
        Assert.Equal(5, taken!.Sequence);           // 最新帧
        Assert.Equal(5, scheduler.SeenFrameCount);  // 预览全量
        Assert.Equal(4, scheduler.DroppedFrameCount); // 旧的 4 帧被覆盖丢弃（FRM-002）
        Assert.Equal(5, previewSeen);
    }

    [Fact]
    public async Task LatestOnly_Waits_For_Next_Frame()
    {
        var scheduler = new FrameScheduler(FrameRoutingStrategy.LatestOnly);
        scheduler.OnFrame(Frame(1));
        _ = await scheduler.TakeNextAsync(CancellationToken.None);

        var takeNext = scheduler.TakeNextAsync(CancellationToken.None);
        Assert.False(takeNext.IsCompleted); // 无帧时挂起
        scheduler.OnFrame(Frame(2));
        var second = await takeNext;
        Assert.Equal(2, second!.Sequence);
    }

    [Fact]
    public async Task Complete_Ends_Finite_Source()
    {
        var scheduler = new FrameScheduler(FrameRoutingStrategy.LatestOnly);
        scheduler.OnFrame(Frame(1));
        scheduler.Complete();
        var first = await scheduler.TakeNextAsync(CancellationToken.None);
        Assert.Equal(1, first!.Sequence);
        var second = await scheduler.TakeNextAsync(CancellationToken.None);
        Assert.Null(second); // 播完
    }

    [Fact]
    public async Task Bounded_Queue_Delivers_In_Order()
    {
        var scheduler = new FrameScheduler(FrameRoutingStrategy.Bounded, boundedCapacity: 4);
        for (var i = 1; i <= 4; i++)
        {
            scheduler.OnFrame(Frame(i));
        }
        for (var i = 1; i <= 4; i++)
        {
            var f = await scheduler.TakeNextAsync(CancellationToken.None);
            Assert.Equal(i, f!.Sequence); // 有界保序
        }
    }

    [Fact]
    public async Task Bounded_Complete_Drains_Then_Ends()
    {
        var scheduler = new FrameScheduler(FrameRoutingStrategy.Bounded, boundedCapacity: 8);
        scheduler.OnFrame(Frame(1));
        scheduler.OnFrame(Frame(2));
        scheduler.Complete();
        Assert.Equal(1, (await scheduler.TakeNextAsync(CancellationToken.None))!.Sequence);
        Assert.Equal(2, (await scheduler.TakeNextAsync(CancellationToken.None))!.Sequence);
        Assert.Null(await scheduler.TakeNextAsync(CancellationToken.None));
    }
}
