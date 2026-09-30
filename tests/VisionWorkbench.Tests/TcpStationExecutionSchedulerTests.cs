using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpStationExecutionSchedulerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SameStation_RunsUpToConfiguredConcurrencyThenQueues(int concurrency)
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
                ct => ExecuteAsync($"req-{index}", ct)))
            .ToArray();
        foreach (var submission in submissions)
            submission.Start!.Invoke();

        await concurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.Equal(concurrency, Volatile.Read(ref active));
        Assert.False(submissions[^1].Completion!.IsCompleted);

        release.TrySetResult(true);
        var results = await Task.WhenAll(submissions.Select(item => item.Completion!))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(concurrency, Volatile.Read(ref peak));
        Assert.Equal(Enumerable.Range(1, concurrency + 1).Select(index => $"req-{index}"), results.Select(result => result.Decision));
        Assert.All(results, result =>
        {
            Assert.Equal(10, result.ProcessedCount);
            Assert.Equal(10, result.TotalCount);
            Assert.Equal("completed", result.Status);
        });
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
        sameStationOtherProject.Start!.Invoke();
        Assert.False(enteredB.Task.IsCompleted);

        var differentStation = scheduler.TryEnqueue(Request("other-station", "project-b") with { StationCode = "station-002" }, stopB.Token, options,
            _ => { enteredOtherStation.TrySetResult(true); return Task.FromResult(Result("other")); });
        differentStation.Start!.Invoke();
        await enteredOtherStation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("other", (await differentStation.Completion!).Decision);
        Assert.False(enteredB.Task.IsCompleted);

        releaseA.TrySetResult(true);
        Assert.Equal("first", (await first.Completion!).Decision);
        Assert.Equal("second", (await sameStationOtherProject.Completion!).Decision);
    }

    [Fact]
    public async Task ExecutionTimeoutStartsAfterQueueWait()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var projectStop = new CancellationTokenSource();
        var enteredFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 1, MaxQueueLength = 4, ExecutionTimeout = TimeSpan.FromSeconds(2),
        };
        var first = scheduler.TryEnqueue(Request("wait-first"), projectStop.Token, options, async ct =>
        {
            enteredFirst.TrySetResult(true);
            await releaseFirst.Task.WaitAsync(ct);
            return Result("first");
        });
        first.Start!.Invoke();
        await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var shortTimeout = options with { ExecutionTimeout = TimeSpan.FromMilliseconds(100) };
        var second = scheduler.TryEnqueue(Request("wait-second"), projectStop.Token, shortTimeout,
            _ => Task.FromResult(Result("second")));
        second.Start!.Invoke();

        await Task.Delay(250);
        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first.Completion!, second.Completion!).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("second", (await second.Completion!).Decision);
    }

    [Fact]
    public async Task CancelingQueuedProjectRequestDoesNotExecuteIt()
    {
        await using var scheduler = new TcpStationExecutionScheduler();
        using var activeStop = new CancellationTokenSource();
        using var queuedStop = new CancellationTokenSource();
        var enteredFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedExecuted = false;
        var options = new TcpStationExecutionScheduler.Options { MaxConcurrency = 1, MaxQueueLength = 4, ExecutionTimeout = TimeSpan.FromSeconds(2) };
        var first = scheduler.TryEnqueue(Request("cancel-first"), activeStop.Token, options, async ct =>
        {
            enteredFirst.TrySetResult(true);
            await releaseFirst.Task.WaitAsync(ct);
            return Result("first");
        });
        first.Start!.Invoke();
        await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var queued = scheduler.TryEnqueue(Request("cancel-queued"), queuedStop.Token, options, _ =>
        {
            queuedExecuted = true;
            return Task.FromResult(Result("should-not-run"));
        });
        queued.Start!.Invoke();
        queuedStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion!);
        releaseFirst.TrySetResult(true);
        await first.Completion!.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(queuedExecuted);
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
