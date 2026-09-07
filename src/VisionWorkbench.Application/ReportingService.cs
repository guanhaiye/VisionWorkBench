using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public sealed record InspectionReport(DateTime FromUtc, DateTime ToUtc, int Total, int Ok, int Ng, int Review, double OkRate, double P50Ms, double P95Ms, double P99Ms);

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
        var rows = await db.InspectionRecords.AsNoTracking().Where(x => x.StartedAt >= fromUtc && x.StartedAt < toUtc).Select(x => new { x.Status, x.TotalElapsedMs }).ToListAsync(ct);
        var values = rows.Select(x => x.TotalElapsedMs).OrderBy(x => x).ToArray();
        var ok = rows.Count(x => string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase));
        return new InspectionReport(fromUtc, toUtc, rows.Count, ok, rows.Count(x => string.Equals(x.Status, "ng", StringComparison.OrdinalIgnoreCase)), rows.Count(x => string.Equals(x.Status, "review_required", StringComparison.OrdinalIgnoreCase)), rows.Count == 0 ? 0 : ok * 100d / rows.Count, Percentile(values, .50), Percentile(values, .95), Percentile(values, .99));
    }

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
