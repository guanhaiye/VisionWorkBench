using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

/// <summary>
/// SOP 产品最终结果的持久化重放器。它不依赖当前检测服务指向哪个产品，
/// 启动时和运行期间都会扫描 FinalizedAtUtc 已写入但尚未发布的 SopRun。
/// 投递语义为 at-least-once，ResultId 是消费者幂等键。
/// </summary>
public sealed class SopProductResultReplayer : IAsyncDisposable, ISopPendingReplayTrigger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SopRunRepository _sopRuns;
    private readonly IResultPublisher _publisher;
    private readonly ILogger<SopProductResultReplayer>? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxBackoff;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly string _workerId = $"replayer:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private Task? _loopTask;
    private int _started;
    private int _disposed;

    public sealed record ReplayBatchResult(int PendingCount, int SucceededCount, int FailedCount)
    {
        public bool HasPending => PendingCount > 0;
    }

    public SopProductResultReplayer(
        SopRunRepository sopRuns,
        IResultPublisher publisher,
        ILogger<SopProductResultReplayer>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? pollInterval = null,
        TimeSpan? maxBackoff = null)
    {
        _sopRuns = sopRuns;
        _publisher = publisher;
        _logger = logger;
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, ct));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>启动后台扫描；第一次扫描在后台立即执行。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _loopTask = Task.Run(RunLoopAsync);
        }
    }

    /// <summary>快速合并唤醒信号；真实扫描只由唯一后台循环执行。</summary>
    public Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start();
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { }
        return Task.CompletedTask;
    }

    /// <summary>执行一次有界扫描，便于启动流程和测试显式等待结果。</summary>
    public async Task<ReplayBatchResult> ReplayPendingOnceAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            var pending = await _sopRuns.QueryPendingFinalizationsAsync(100, cancellationToken);
            var replayed = 0;
            foreach (var run in pending)
            {
                if (await ReplayOneAsync(run, cancellationToken))
                {
                    replayed++;
                }
            }
            return new ReplayBatchResult(pending.Count, replayed, pending.Count - replayed);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task RunLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            ReplayBatchResult? result = null;
            var failed = false;
            try
            {
                result = await ReplayPendingOnceAsync(_cts.Token);
                failed = result.FailedCount > 0;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SOP 产品结果重放扫描失败");
                failed = true;
            }

            var delay = failed ? backoff : _pollInterval;
            if (failed)
            {
                backoff = NextBackoff(backoff);
            }
            else
            {
                backoff = TimeSpan.FromSeconds(1);
            }
            try
            {
                await WaitForNextScanAsync(delay, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SOP 产品结果重放等待失败");
            }
        }
    }

    /// <summary>
    /// 等待轮询周期或外部唤醒。外部唤醒优先，且会取消当前等待任务，避免留下未观察异常。
    /// </summary>
    private async Task WaitForNextScanAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = _delayAsync(delay, waitCts.Token);
        var wakeTask = _wakeSignal.WaitAsync(waitCts.Token);
        var completed = await Task.WhenAny(delayTask, wakeTask);
        waitCts.Cancel();

        if (completed == wakeTask)
        {
            await ObserveCanceledAsync(delayTask);
            return;
        }

        try
        {
            await delayTask;
        }
        finally
        {
            await ObserveCanceledAsync(wakeTask);
        }
    }

    private static async Task ObserveCanceledAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    public static TimeSpan CalculateBackoff(TimeSpan current, TimeSpan maxBackoff)
    {
        var maxSeconds = Math.Max(1, maxBackoff.TotalSeconds);
        var nextSeconds = Math.Min(maxSeconds, Math.Max(1, current.TotalSeconds) * 2);
        return TimeSpan.FromSeconds(nextSeconds);
    }

    private TimeSpan NextBackoff(TimeSpan current) => CalculateBackoff(current, _maxBackoff);

    private async Task<bool> ReplayOneAsync(SopRunEntity run, CancellationToken ct)
    {
        var claimId = $"{_workerId}:{run.Id}:{Guid.NewGuid():N}";
        if (!await _sopRuns.TryClaimPendingFinalizationAsync(run.Id, claimId, ct: ct))
        {
            return false;
        }

        try
        {
            var envelope = DeserializeEnvelope(run)
                ?? throw new InvalidOperationException($"SopRun {run.Id} 的最终结果 JSON 无法解析");
            await _publisher.PublishProductAsync(envelope, ct);
            await _sopRuns.MarkFinalPublishedAsync(run.Id, claimId, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _sopRuns.ReleaseFinalizationClaimAsync(run.Id, claimId, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _sopRuns.ReleaseFinalizationClaimAsync(run.Id, claimId, CancellationToken.None);
            _logger?.LogWarning(ex, "SOP 产品结果重放失败，将在下一轮重试: run={RunId}", run.Id);
            return false;
        }
    }

    private static ProductResultEnvelope? DeserializeEnvelope(SopRunEntity run)
    {
        if (!string.IsNullOrWhiteSpace(run.FinalResultJson))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<ProductResultEnvelope>(run.FinalResultJson, JsonOptions);
                if (envelope is not null)
                {
                    return envelope;
                }
            }
            catch (JsonException)
            {
                // 兼容旧版本/损坏信封时尝试用 SopRun 字段重建产品信封。
            }
        }

        if (string.IsNullOrWhiteSpace(run.FinalDecisionJson))
        {
            return null;
        }
        var decision = JsonSerializer.Deserialize<DecisionResult>(run.FinalDecisionJson, JsonOptions);
        if (decision is null)
        {
            return null;
        }
        return new ProductResultEnvelope
        {
            ResultId = $"sop-product:{run.Id}:final",
            ProjectId = run.ProjectId,
            StationCode = run.StationCode,
            TaskId = run.TaskId,
            BatchId = run.BatchId,
            SopRunId = run.Id,
            CycleId = run.CycleId,
            Timestamp = new DateTimeOffset(run.FinalizedAtUtc ?? DateTime.UtcNow, TimeSpan.Zero),
            Decision = decision,
            WorkflowResultJson = "{}",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex) { _logger?.LogError(ex, "SOP 产品结果重放器退出失败"); }
        }
        _scanGate.Dispose();
        _wakeSignal.Dispose();
        _cts.Dispose();
    }
}
