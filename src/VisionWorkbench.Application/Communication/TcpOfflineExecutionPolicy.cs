namespace VisionWorkbench.Application.Communication;

/// <summary>Execution-time inputs captured before a request enters its station queue.</summary>
public sealed record TcpExecutionPlan(
    TimeSpan ExecutionTimeout,
    IReadOnlyList<string>? ImageFiles = null);

public readonly record struct TcpExecutionProgressSnapshot(
    int TotalCount, int ProcessedCount, int SkippedCount)
{
    public int UnprocessedCount => Math.Max(0, TotalCount - ProcessedCount - SkippedCount);
}

/// <summary>Thread-safe image batch progress retained with the idempotent TCP request.</summary>
public sealed class TcpExecutionProgress(int totalCount)
{
    private int _processed;
    private int _skipped;

    public int TotalCount { get; } = Math.Max(0, totalCount);
    public void ReportProcessed(int count = 1) => Interlocked.Add(ref _processed, Math.Max(0, count));
    public void ReportSkipped(int count = 1) => Interlocked.Add(ref _skipped, Math.Max(0, count));
    public void SetSkippedCount(int count) => Interlocked.Exchange(ref _skipped, Math.Max(0, count));

    public TcpExecutionProgressSnapshot Snapshot() => new(
        TotalCount,
        Math.Min(TotalCount, Math.Max(0, Volatile.Read(ref _processed))),
        Math.Min(TotalCount, Math.Max(0, Volatile.Read(ref _skipped))));
}

/// <summary>Bridges each completed inference record to the request-wide progress snapshot.</summary>
public sealed class TcpOfflineExecutionProgressTracker(TcpExecutionProgress? requestProgress)
{
    private int _processedCount;

    public int ProcessedCount => Math.Max(0, Volatile.Read(ref _processedCount));

    public void ReportProcessed()
    {
        Interlocked.Increment(ref _processedCount);
        requestProgress?.ReportProcessed();
    }
}
public static class TcpOfflineExecutionPolicy
{
    /// <summary>
    /// Treats the configured timeout as one-run startup/coordination allowance, then
    /// adds a per-file budget based on measured inference latency (or a conservative
    /// cold-history estimate). The scheduler starts this timer only after dequeue.
    /// </summary>
    public static TimeSpan CalculateTimeout(
        int fileCount, TimeSpan configuredTimeout, TimeSpan? measuredInferencePerImage)
    {
        var baseMilliseconds = Math.Max(1_000, configuredTimeout.TotalMilliseconds);
        if (fileCount <= 0) return TimeSpan.FromMilliseconds(baseMilliseconds);

        var measured = measuredInferencePerImage is { } duration && duration > TimeSpan.Zero
            ? duration.TotalMilliseconds
            : 500;
        var perImageMilliseconds = Math.Max(1_500, measured * 2 + 500);
        var totalMilliseconds = baseMilliseconds + fileCount * perImageMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(totalMilliseconds, TimeSpan.MaxValue.TotalMilliseconds));
    }

    public static TcpTaskExecutionResult CreateResult(
        TcpExecutionProgressSnapshot progress,
        long count,
        string decision,
        long recordId)
    {
        var complete = progress.TotalCount > 0 && progress.ProcessedCount + progress.SkippedCount == progress.TotalCount;
        var status = progress.TotalCount == 0 ? "failed" : complete && progress.SkippedCount == 0 ? "completed" : "partial_failure";
        var summary = progress.SkippedCount == 0
            ? decision
            : $"{decision}; skipped {progress.SkippedCount}/{progress.TotalCount}";
        return new TcpTaskExecutionResult(
            status,
            count,
            summary,
            recordId,
            progress.ProcessedCount,
            progress.SkippedCount,
            progress.TotalCount);
    }
}
