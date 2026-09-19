using System.Diagnostics;
using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpStationExecutionSchedulerTests
{
    [Fact]
    public async Task SameStation_ThreeSecondGap_AllowsFiveSecondExecutionsToOverlap()
    {
        await using var scheduler = new TcpStationExecutionScheduler(new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 3,
            MaxQueueLength = 20,
            ExecutionTimeout = TimeSpan.FromSeconds(30),
        });
        using var projectStop = new CancellationTokenSource();
        var active = 0;
        var maxActive = 0;

        async Task<TcpTaskExecutionResult> Execute(TcpTaskExecutionRequest request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            while (true)
            {
                var previous = Volatile.Read(ref maxActive);
                if (now <= previous || Interlocked.CompareExchange(ref maxActive, now, previous) == previous) break;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new TcpTaskExecutionResult("completed", request.TaskId, request.RequestId);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var started = Stopwatch.StartNew();
        var first = scheduler.TryEnqueue(
            Request("req-001"), projectStop.Token, ct => Execute(Request("req-001"), ct));
        Assert.Equal(TcpExecutionAdmission.Accepted, first.Admission);
        first.Start!.Invoke();

        await Task.Delay(TimeSpan.FromSeconds(3));
        var second = scheduler.TryEnqueue(
            Request("req-002"), projectStop.Token, ct => Execute(Request("req-002"), ct));
        Assert.Equal(TcpExecutionAdmission.Accepted, second.Admission);
        second.Start!.Invoke();

        var results = await Task.WhenAll(first.Completion!, second.Completion!);
        started.Stop();

        Assert.True(maxActive >= 2, "同工位第二个请求没有和第一个请求重叠执行");
        Assert.Contains(results, result => result.Decision == "req-001");
        Assert.Contains(results, result => result.Decision == "req-002");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(9), $"执行耗时 {started.Elapsed}");
    }

    [Fact]
    public async Task DuplicateRequestId_IsProcessingThenReturnsCachedResult()
    {
        await using var scheduler = new TcpStationExecutionScheduler(new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 1,
            MaxQueueLength = 20,
            ExecutionTimeout = TimeSpan.FromSeconds(5),
        });
        using var projectStop = new CancellationTokenSource();
        var request = Request("same-request");
        var first = scheduler.TryEnqueue(request, projectStop.Token,
            _ => Task.FromResult(new TcpTaskExecutionResult("completed", 1, "first")));
        Assert.Equal(TcpExecutionAdmission.Accepted, first.Admission);
        var duplicateWhileRunning = scheduler.TryEnqueue(request, projectStop.Token,
            _ => Task.FromResult(new TcpTaskExecutionResult("completed", 2, "duplicate")));
        Assert.Equal(TcpExecutionAdmission.Processing, duplicateWhileRunning.Admission);
        first.Start!.Invoke();
        var result = await first.Completion!;

        var duplicateAfterCompletion = scheduler.TryEnqueue(request, projectStop.Token,
            _ => Task.FromResult(new TcpTaskExecutionResult("completed", 3, "duplicate")));
        Assert.Equal(TcpExecutionAdmission.Completed, duplicateAfterCompletion.Admission);
        Assert.Equal(result, duplicateAfterCompletion.CachedResult);
    }

    [Fact]
    public async Task SameStationName_IsolatedByProject_AndQueueFullIsPerProject()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var stopA = new CancellationTokenSource();
        using var stopB = new CancellationTokenSource();
        var releaseA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aOptions = new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 1, MaxQueueLength = 1, ExecutionTimeout = TimeSpan.FromSeconds(10),
        };
        var bOptions = new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 2, MaxQueueLength = 2, ExecutionTimeout = TimeSpan.FromSeconds(10),
        };

        var a1 = scheduler.TryEnqueue(Request("a-1", "project-a"), stopA.Token, aOptions,
            async ct => { enteredA.TrySetResult(true); await releaseA.Task.WaitAsync(ct); return Result("a-1"); });
        a1.Start!.Invoke();
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var a2 = scheduler.TryEnqueue(Request("a-2", "project-a"), stopA.Token, aOptions,
            _ => Task.FromResult(Result("a-2")));
        var a3 = scheduler.TryEnqueue(Request("a-3", "project-a"), stopA.Token, aOptions,
            _ => Task.FromResult(Result("a-3")));
        Assert.Equal(TcpExecutionAdmission.Accepted, a1.Admission);
        Assert.Equal(TcpExecutionAdmission.Accepted, a2.Admission);
        Assert.Equal(TcpExecutionAdmission.QueueFull, a3.Admission);
        a2.Start!.Invoke();

        var b1 = scheduler.TryEnqueue(Request("b-1", "project-b"), stopB.Token, bOptions,
            async ct => { await releaseB.Task.WaitAsync(ct); return Result("b-1"); });
        b1.Start!.Invoke();
        var b2 = scheduler.TryEnqueue(Request("b-2", "project-b"), stopB.Token, bOptions,
            async ct => { await releaseB.Task.WaitAsync(ct); return Result("b-2"); });
        Assert.Equal(TcpExecutionAdmission.Accepted, b1.Admission);
        Assert.Equal(TcpExecutionAdmission.Accepted, b2.Admission);
        b2.Start!.Invoke();
        releaseB.TrySetResult(true);
        await Task.WhenAll(b1.Completion!, b2.Completion!).WaitAsync(TimeSpan.FromSeconds(3));

        releaseA.TrySetResult(true);
        await Task.WhenAll(a1.Completion!, a2.Completion!).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Timeout_IsTerminalAndDuplicateReportsTimeout()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var stop = new CancellationTokenSource();
        var request = Request("timeout-1");
        var options = new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 1, MaxQueueLength = 1, ExecutionTimeout = TimeSpan.FromMilliseconds(50),
        };
        var submission = scheduler.TryEnqueue(request, stop.Token, options,
            ct => Task.Delay(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => Result("never"), ct));
        submission.Start!.Invoke();
        await Assert.ThrowsAsync<TcpExecutionTimeoutException>(() => submission.Completion!);

        var duplicate = scheduler.TryEnqueue(request, stop.Token, options,
            _ => Task.FromResult(Result("duplicate")));
        Assert.Equal(TcpExecutionAdmission.Completed, duplicate.Admission);
        Assert.Equal("execution_timeout", duplicate.TerminalCode);
    }

    [Fact]
    public async Task TriggerFrameDistributor_BroadcastsEligibleFramesWithoutHistoryGrowth()
    {
        var distributor = new TriggerFrameDistributor();
        var baseline = DateTimeOffset.UtcNow;
        for (var index = 0; index < 1000; index++)
            distributor.Publish(Frame(index, baseline.AddMilliseconds(index)));
        Assert.True(distributor.HasBufferedFrame);

        var first = distributor.WaitForFrameAsync(baseline.AddMilliseconds(1000));
        var second = distributor.WaitForFrameAsync(baseline.AddMilliseconds(1000));
        distributor.Publish(Frame(1001, baseline.AddMilliseconds(1001)));
        Assert.Equal(1001, (await first).Sequence);
        Assert.Equal(1001, (await second).Sequence);
        Assert.Equal(0, distributor.WaitingCount);

        var earlier = distributor.WaitForFrameAsync(baseline.AddMilliseconds(1002));
        var later = distributor.WaitForFrameAsync(baseline.AddMilliseconds(1004));
        distributor.Publish(Frame(1002, baseline.AddMilliseconds(1002)));
        Assert.Equal(1002, (await earlier).Sequence);
        Assert.False(later.IsCompleted);
        distributor.Publish(Frame(1004, baseline.AddMilliseconds(1004)));
        Assert.Equal(1004, (await later).Sequence);
    }

    private static TcpTaskExecutionRequest Request(string requestId, string projectCode = "tcp-test") => new(
        projectCode, 100, "station-001", "测试任务", requestId, requestId, DateTimeOffset.UtcNow);

    private static TcpTaskExecutionResult Result(string requestId) =>
        new("completed", 1, requestId);

    private static VideoFrame Frame(long sequence, DateTimeOffset timestamp) =>
        new(sequence, timestamp, 1, 1, [0, 0, 0]);
}
