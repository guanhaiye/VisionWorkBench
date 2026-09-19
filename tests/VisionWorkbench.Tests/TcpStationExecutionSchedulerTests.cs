using System.Diagnostics;
using VisionWorkbench.Application.Communication;
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

    private static TcpTaskExecutionRequest Request(string requestId) => new(
        "tcp-test", 100, "station-001", "测试任务", requestId, requestId, DateTimeOffset.UtcNow);
}
