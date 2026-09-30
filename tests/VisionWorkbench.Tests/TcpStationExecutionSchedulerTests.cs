using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpStationExecutionSchedulerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SameStation_RunsUpToConfiguredConcurrencyAndRejectsOverflow(int concurrency)
    {
        await using var scheduler = new TcpStationExecutionScheduler(new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = concurrency,
            MaxQueueLength = 8,
            ExecutionTimeout = TimeSpan.FromSeconds(5),
        });
        using var projectStop = new CancellationTokenSource();
        var concurrencyReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;

        async Task<TcpTaskExecutionResult> ExecuteAsync(string id, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref peak);
                if (observed >= now) break;
            } while (Interlocked.CompareExchange(ref peak, now, observed) != observed);
            if (now == concurrency) concurrencyReached.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref active);
            return new TcpTaskExecutionResult("completed", 10, id, ProcessedCount: 10, TotalCount: 10);
        }

        var submissions = Enumerable.Range(1, concurrency + 1)
            .Select(index => scheduler.TryEnqueue(Request($"req-{index}"), projectStop.Token,
                new TcpStationExecutionScheduler.Options
                {
                    MaxConcurrency = concurrency,
                    MaxQueueLength = 8,
                    ExecutionTimeout = TimeSpan.FromSeconds(5),
                }, ct => ExecuteAsync($"req-{index}", ct)))
            .ToArray();
        var admitted = submissions.Take(concurrency).ToArray();
        foreach (var submission in admitted)
        {
            Assert.Equal(TcpExecutionAdmission.Accepted, submission.Admission);
            submission.Start!.Invoke();
        }
        var busy = submissions[^1];
        Assert.Equal(TcpExecutionAdmission.Busy, busy.Admission);
        Assert.Equal("station_busy", busy.CachedResult?.Status);
        Assert.Contains("未排队", busy.CachedResult?.Decision);
        Assert.Null(busy.Completion);

        var busyDuplicate = scheduler.TryEnqueue(Request($"req-{concurrency + 1}"), projectStop.Token,
            _ => throw new InvalidOperationException("重复 busy request 不应执行"));
        Assert.Equal(TcpExecutionAdmission.Completed, busyDuplicate.Admission);
        Assert.Equal(busy.CachedResult, busyDuplicate.CachedResult);

        await concurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(concurrency, Volatile.Read(ref active));
        release.TrySetResult(true);
        var results = await Task.WhenAll(admitted.Select(item => item.Completion!))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(concurrency, Volatile.Read(ref peak));
        Assert.Equal(Enumerable.Range(1, concurrency).Select(index => $"req-{index}"), results.Select(result => result.Decision));
        Assert.All(results, result =>
        {
            Assert.Equal(10, result.ProcessedCount);
            Assert.Equal(10, result.TotalCount);
            Assert.Equal("completed", result.Status);
        });

        var afterRelease = scheduler.TryEnqueue(Request("after-release"), projectStop.Token,
            new TcpStationExecutionScheduler.Options { MaxConcurrency = concurrency },
            _ => Task.FromResult(Result("after-release")));
        Assert.Equal(TcpExecutionAdmission.Accepted, afterRelease.Admission);
        afterRelease.Start!.Invoke();
        Assert.Equal("after-release", (await afterRelease.Completion!).Decision);
    }

    [Fact]
    public async Task SharedStation_ExpandsWorkersWhenLaterProjectRequestsHigherConcurrency()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var stop = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.TryEnqueue(Request("expand-first", "project-a"), stop.Token,
            new TcpStationExecutionScheduler.Options { MaxConcurrency = 1 }, async ct =>
            {
                firstEntered.TrySetResult(true);
                await release.Task.WaitAsync(ct);
                return Result("first");
            });
        first.Start!.Invoke();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = scheduler.TryEnqueue(Request("expand-second", "project-b"), stop.Token,
            new TcpStationExecutionScheduler.Options { MaxConcurrency = 2 }, ct =>
            {
                secondEntered.TrySetResult(true);
                return Task.FromResult(Result("second"));
            });
        second.Start!.Invoke();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(first.Completion!.IsCompleted);
        Assert.Equal("second", (await second.Completion!).Decision);

        release.TrySetResult(true);
        Assert.Equal("first", (await first.Completion!).Decision);
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
    public async Task SameStationName_IsSharedAcrossProjects_ButDifferentStationsCanRunConcurrently()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var stopA = new CancellationTokenSource();
        using var stopB = new CancellationTokenSource();
        var enteredA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredOtherStation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 1, MaxQueueLength = 8, ExecutionTimeout = TimeSpan.FromSeconds(10),
        };

        var first = scheduler.TryEnqueue(Request("project-a-1", "project-a"), stopA.Token, options,
            async ct => { enteredA.TrySetResult(true); await releaseA.Task.WaitAsync(ct); return Result("first"); });
        first.Start!.Invoke();
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var sameStationOtherProject = scheduler.TryEnqueue(Request("project-b-1", "project-b"), stopB.Token, options,
            _ => { enteredB.TrySetResult(true); return Task.FromResult(Result("second")); });
        Assert.Equal(TcpExecutionAdmission.Busy, sameStationOtherProject.Admission);
        Assert.Equal("station_busy", sameStationOtherProject.CachedResult?.Status);
        Assert.False(enteredB.Task.IsCompleted);

        var differentStation = scheduler.TryEnqueue(Request("other-station", "project-b") with { StationCode = "station-002" }, stopB.Token, options,
            _ => { enteredOtherStation.TrySetResult(true); return Task.FromResult(Result("other")); });
        differentStation.Start!.Invoke();
        await enteredOtherStation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("other", (await differentStation.Completion!).Decision);
        Assert.False(enteredB.Task.IsCompleted);

        releaseA.TrySetResult(true);
        Assert.Equal("first", (await first.Completion!).Decision);
        Assert.Equal("station_busy", sameStationOtherProject.CachedResult?.Status);
    }

    [Fact]
    public async Task CancelingAdmittedRequestReleasesCapacityForNextRequest()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var activeStop = new CancellationTokenSource();
        var enteredFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 1, MaxQueueLength = 4, ExecutionTimeout = TimeSpan.FromSeconds(2),
        };
        var first = scheduler.TryEnqueue(Request("cancel-first"), activeStop.Token, options, async ct =>
        {
            enteredFirst.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result("unreachable");
        });
        first.Start!.Invoke();
        await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var overflow = scheduler.TryEnqueue(Request("while-first-active"), activeStop.Token, options,
            _ => Task.FromResult(Result("must-not-run")));
        Assert.Equal(TcpExecutionAdmission.Busy, overflow.Admission);
        activeStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Completion!);

        var afterCancellation = scheduler.TryEnqueue(Request("after-cancel"), CancellationToken.None, options,
            _ => Task.FromResult(Result("after-cancel")));
        Assert.Equal(TcpExecutionAdmission.Accepted, afterCancellation.Admission);
        afterCancellation.Start!.Invoke();
        Assert.Equal("after-cancel", (await afterCancellation.Completion!).Decision);
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
