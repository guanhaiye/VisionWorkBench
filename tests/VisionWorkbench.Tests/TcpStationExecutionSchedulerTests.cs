using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpStationExecutionSchedulerTests
{
    [Fact]
    public async Task SameStation_RequestsRunInOrderEvenWhenConcurrencySettingIsThree()
    {
        await using var scheduler = new TcpStationExecutionScheduler(new TcpStationExecutionScheduler.Options
        {
            MaxConcurrency = 3,
            MaxQueueLength = 20,
            ExecutionTimeout = TimeSpan.FromSeconds(5),
        });
        using var projectStop = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.TryEnqueue(Request("req-001"), projectStop.Token, async ct =>
        {
            firstEntered.TrySetResult(true);
            await releaseFirst.Task.WaitAsync(ct);
            return Result("req-001");
        });
        first.Start!.Invoke();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = scheduler.TryEnqueue(Request("req-002"), projectStop.Token, _ =>
        {
            secondEntered.TrySetResult(true);
            return Task.FromResult(Result("req-002"));
        });
        second.Start!.Invoke();
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.TrySetResult(true);

        Assert.Equal("req-001", (await first.Completion!).Decision);
        Assert.Equal("req-002", (await second.Completion!).Decision);
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
            MaxConcurrency = 2, MaxQueueLength = 8, ExecutionTimeout = TimeSpan.FromSeconds(10),
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
            MaxQueueLength = 4, ExecutionTimeout = TimeSpan.FromSeconds(2),
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
        var options = new TcpStationExecutionScheduler.Options { MaxQueueLength = 4, ExecutionTimeout = TimeSpan.FromSeconds(2) };
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
