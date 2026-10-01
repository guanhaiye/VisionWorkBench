using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public sealed record InspectionReport(DateTime FromUtc, DateTime ToUtc, int Total, int Ok, int Ng, int Review, double OkRate, double P50Ms, double P95Ms, double P99Ms);

public sealed record DashboardQuery(long? TaskId, string? Status, DateTime FromUtc, DateTime ToUtc);
public sealed record DashboardBucket(DateTime StartUtc, int Total, int Ok, int Ng, int Review, double OkRate);
public sealed record DashboardTrendSeries(long TaskId, string Name, IReadOnlyList<DashboardBucket> Points);
public sealed record DashboardGroup(string Name, int Total, int Ok, int Ng, int Review, double OkRate);
public sealed record DashboardPerformance(double AverageMs, double P50Ms, double P95Ms, double P99Ms);
public sealed record DashboardSopSummary(int ProductCycles, int Ok, int Timeout, int WrongOrder, int Review, int Aborted);
public sealed record DashboardReport(
    DateTime FromUtc,
    DateTime ToUtc,
    int Total,
    int Ok,
    int Ng,
    int Review,
    int Error,
    int Processing,
    double OkRate,
    DashboardPerformance Performance,
    IReadOnlyList<DashboardBucket> Trend,
    IReadOnlyList<DashboardGroup> Statuses,
    IReadOnlyList<DashboardGroup> Tasks,
    DashboardSopSummary? Sop,
    IReadOnlyList<DashboardGroup> SopFailures)
{
    public IReadOnlyList<DashboardTrendSeries> TrendSeries { get; init; } = [];
}

public sealed class ReportingService(VisionDbContextFactory factory)
{
    public async Task<ReportJobEntity> QueueAsync(string format, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (fromUtc >= toUtc) throw new ArgumentException("Report start must be before report end");
        var normalized = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        if (normalized is not ("csv" or "xlsx")) throw new ArgumentException("Only CSV and XLSX report formats are supported");
        await using var db = factory.CreateDbContext();
        var job = new ReportJobEntity { Format = normalized, FromUtc = fromUtc, ToUtc = toUtc };
        db.ReportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task RunCsvJobAsync(long jobId, string path, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var job = await db.ReportJobs.SingleOrDefaultAsync(x => x.Id == jobId, ct)
            ?? throw new InvalidOperationException("Report job does not exist");
        job.Status = "running";
        await db.SaveChangesAsync(ct);
        try
        {
            await ExportCsvAsync(path, job.FromUtc, job.ToUtc, ct);
            job.Status = "completed";
            job.OutputPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.OutputPath = ex.Message[..Math.Min(260, ex.Message.Length)];
            throw;
        }
        finally
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<InspectionReport> BuildAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var rows = await db.InspectionRecords.AsNoTracking()
            .Where(x => x.StartedAt >= fromUtc && x.StartedAt < toUtc)
            .Select(x => new { x.Status, x.TotalElapsedMs })
            .ToListAsync(ct);
        // processing 是 SOP 产品周期的中间帧，不进入质量统计分母，也不作为错误。
        var terminalRows = rows.Where(x => x.Status is "ok" or "ng" or "review_required").ToArray();
        var values = terminalRows.Select(x => x.TotalElapsedMs).OrderBy(x => x).ToArray();
        var ok = terminalRows.Count(x => string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase));
        return new InspectionReport(fromUtc, toUtc, terminalRows.Length, ok, terminalRows.Count(x => string.Equals(x.Status, "ng", StringComparison.OrdinalIgnoreCase)), terminalRows.Count(x => string.Equals(x.Status, "review_required", StringComparison.OrdinalIgnoreCase)), terminalRows.Length == 0 ? 0 : ok * 100d / terminalRows.Length, Percentile(values, .50), Percentile(values, .95), Percentile(values, .99));
    }

    /// <summary>构建历史数据看板。只读取聚合所需列；SOP 记录按产品周期去重。</summary>
    public async Task<DashboardReport> BuildDashboardAsync(DashboardQuery query, CancellationToken ct = default)
    {
        if (query.FromUtc >= query.ToUtc) throw new ArgumentException("统计起止时间无效");
        await using var db = factory.CreateDbContext();
        var records = await db.InspectionRecords.AsNoTracking()
            .Where(x => x.StartedAt >= query.FromUtc && x.StartedAt < query.ToUtc
                && (query.TaskId == null || x.TaskId == query.TaskId)
                && (query.Status == null || x.Status == query.Status))
            .Select(x => new DashboardRecordRow(
                x.TaskId, x.StationCode, x.StartedAt, x.Status, x.TotalElapsedMs, x.SopRunId))
            .ToListAsync(ct);
        var taskNames = await db.Tasks.AsNoTracking()
            .Where(x => records.Select(r => r.TaskId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var sopRunIds = records.Where(x => x.SopRunId is not null).Select(x => x.SopRunId!.Value).Distinct().ToArray();
        var sopRuns = sopRunIds.Length == 0
            ? []
            : await db.SopRuns.AsNoTracking()
                .Where(x => sopRunIds.Contains(x.Id))
                .Select(x => new DashboardSopRow(x.Id, x.TaskId, x.StartedAtUtc, x.FinalStatus, x.Status))
                .ToListAsync(ct);
        var sopStepFailures = sopRunIds.Length == 0
            ? []
            : await db.SopStepResults.AsNoTracking()
                .Where(x => sopRunIds.Contains(x.SopRunId))
                .Where(x => x.Status == "Failed" || x.Status == "NgTimeout"
                    || x.Status == "NgConditionFailed" || x.Status == "NgWrongOrder")
                .GroupBy(x => x.StepId)
                .Select(g => new DashboardGroup(g.Key, g.Count(), 0, g.Count(), 0, 0))
                .OrderByDescending(x => x.Total).ThenBy(x => x.Name)
                .Take(10)
                .ToListAsync(ct);

        var sopById = sopRuns.ToDictionary(x => x.Id);
        // 普通检测一条记录计数；SOP 检测只按产品周期终态计数，避免步骤帧重复统计。
        var normal = records.Where(x => x.SopRunId is null).ToArray();
        var sopFinal = sopRuns.Where(x => IsFinalStatus(x.FinalStatus) || IsFinalStatus(x.Status))
            .Select(x => new DashboardRecordRow(x.TaskId, "", x.StartedAtUtc, NormalizeSopStatus(x), 0, x.Id))
            .ToArray();
        var effective = normal.Concat(sopFinal).ToArray();
        var terminal = effective.Where(x => IsQualityStatus(x.Status)).ToArray();
        var values = terminal.Where(x => x.TotalElapsedMs > 0).Select(x => x.TotalElapsedMs).OrderBy(x => x).ToArray();
        var ok = terminal.Count(x => x.Status == "ok");
        var ng = terminal.Count(x => x.Status == "ng");
        var review = terminal.Count(x => x.Status == "review_required");
        var error = effective.Count(x => x.Status == "error");
        var processing = effective.Count(x => x.Status == "processing");
        var statuses = new[]
        {
            new DashboardGroup("OK", ok, ok, 0, 0, 100),
            new DashboardGroup("NG", ng, 0, ng, 0, 0),
            new DashboardGroup("待复核", review, 0, 0, review, 0),
        };
        var trend = BuildTrend(effective, query.FromUtc, query.ToUtc);
        var trendSeries = effective
            .GroupBy(row => row.TaskId)
            .Select(group => new DashboardTrendSeries(
                group.Key,
                BuildTaskDisplayName(group.Key, group, taskNames),
                BuildTrend(group.ToArray(), query.FromUtc, query.ToUtc)))
            .Where(series => series.Points.Any(point => point.Total > 0))
            .OrderBy(series => series.Name, StringComparer.Ordinal)
            .ToArray();
        var tasks = effective.GroupBy(x => x.TaskId).Select(g =>
        {
            var rows = g.ToArray();
            var q = rows.Where(x => IsQualityStatus(x.Status)).ToArray();
            var good = q.Count(x => x.Status == "ok");
            return new DashboardGroup(BuildTaskDisplayName(g.Key, g, taskNames), q.Length, good,
                q.Count(x => x.Status == "ng"), q.Count(x => x.Status == "review_required"), Rate(good, q.Length));
        }).OrderByDescending(x => x.Total).ThenBy(x => x.Name).Take(10).ToArray();
        DashboardSopSummary? sop = null;
        if (sopRuns.Count > 0)
        {
            sop = new DashboardSopSummary(
                sopRuns.Count,
                sopRuns.Count(x => NormalizeSopStatus(x) == "ok"),
                sopRuns.Count(x => NormalizeSopStatus(x) == "ng" && IsWrongOrderOrTimeout(x.Status, x.FinalStatus, "timeout")),
                sopRuns.Count(x => IsWrongOrderOrTimeout(x.Status, x.FinalStatus, "wrongorder")),
                sopRuns.Count(x => NormalizeSopStatus(x) == "review_required"),
                sopRuns.Count(x => IsWrongOrderOrTimeout(x.Status, x.FinalStatus, "aborted")));
        }
        return new DashboardReport(query.FromUtc, query.ToUtc, terminal.Length, ok, ng, review, error, processing,
            Rate(ok, terminal.Length), new DashboardPerformance(values.Length == 0 ? 0 : values.Average(), Percentile(values, .50), Percentile(values, .95), Percentile(values, .99)),
            trend, statuses, tasks, sop, sopStepFailures)
        {
            TrendSeries = trendSeries,
        };
    }

    private sealed record DashboardRecordRow(long TaskId, string StationCode, DateTime StartedAt, string Status, double TotalElapsedMs, long? SopRunId);
    private sealed record DashboardSopRow(long Id, long TaskId, DateTime StartedAtUtc, string? FinalStatus, string Status);

    private static string BuildTaskDisplayName(
        long taskId,
        IEnumerable<DashboardRecordRow> rows,
        IReadOnlyDictionary<long, string> taskNames)
    {
        var name = taskNames.GetValueOrDefault(taskId, $"任务 {taskId}");
        var stationCode = rows
            .Select(row => row.StationCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        return string.IsNullOrWhiteSpace(stationCode) ? name : $"[{stationCode}] {name}";
    }

    private static IReadOnlyList<DashboardBucket> BuildTrend(DashboardRecordRow[] rows, DateTime fromUtc, DateTime toUtc)
    {
        var span = toUtc - fromUtc;
        var step = span.TotalDays <= 2 ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var first = step == TimeSpan.FromHours(1)
            ? new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc)
            : fromUtc.Date;
        var buckets = new List<DashboardBucket>();
        for (var start = first; start < toUtc; start += step)
        {
            var slice = rows.Where(x => x.StartedAt >= start && x.StartedAt < start + step && IsQualityStatus(x.Status)).ToArray();
            var good = slice.Count(x => x.Status == "ok");
            buckets.Add(new DashboardBucket(start, slice.Length, good, slice.Count(x => x.Status == "ng"),
                slice.Count(x => x.Status == "review_required"), Rate(good, slice.Length)));
        }
        return buckets;
    }

    private static bool IsQualityStatus(string status) => status is "ok" or "ng" or "review_required";
    private static bool IsFinalStatus(string? status) => status?.ToLowerInvariant() is "completedok" or "ngtimeout" or "ngconditionfailed" or "ngwrongorder" or "reviewrequired" or "interrupted" or "aborted" or "ok" or "ng" or "review_required";
    private static string NormalizeSopStatus(DashboardSopRow row)
    {
        var status = (row.FinalStatus ?? row.Status).ToLowerInvariant();
        return status switch
        {
            "completedok" or "ok" => "ok",
            "reviewrequired" or "review_required" => "review_required",
            "ngtimeout" or "ngconditionfailed" or "ngwrongorder" or "ng" => "ng",
            "interrupted" or "aborted" => "error",
            _ => "processing",
        };
    }
    private static bool IsWrongOrderOrTimeout(string status, string? finalStatus, string kind)
    {
        var text = $"{status} {finalStatus}".ToLowerInvariant();
        return kind switch
        {
            "timeout" => text.Contains("timeout") || text.Contains("conditionfailed"),
            "wrongorder" => text.Contains("wrongorder"),
            "aborted" => text.Contains("aborted") || text.Contains("interrupted"),
            _ => false,
        };
    }
    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : numerator * 100d / denominator;

    public async Task ExportCsvAsync(string path, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var report = await BuildAsync(fromUtc, toUtc, ct);
        var text = new StringBuilder().AppendLine("FromUtc,ToUtc,Total,Ok,Ng,Review,OkRatePercent,P50Ms,P95Ms,P99Ms")
            .AppendLine(string.Join(',', report.FromUtc.ToString("O"), report.ToUtc.ToString("O"), report.Total, report.Ok, report.Ng, report.Review, report.OkRate.ToString("F2", CultureInfo.InvariantCulture), report.P50Ms.ToString("F3", CultureInfo.InvariantCulture), report.P95Ms.ToString("F3", CultureInfo.InvariantCulture), report.P99Ms.ToString("F3", CultureInfo.InvariantCulture))).ToString();
        await File.WriteAllTextAsync(path, "\uFEFF" + text, Encoding.UTF8, ct);
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1);
        return values[index];
    }
}
