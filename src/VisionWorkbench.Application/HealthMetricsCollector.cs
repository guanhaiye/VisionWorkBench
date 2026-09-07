using System.Diagnostics;
using System.Text.Json;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public sealed record GpuMemorySnapshot(ulong UsedBytes, ulong TotalBytes, string Source);

/// <summary>Collects lightweight host/application metrics without making GPU tooling mandatory.</summary>
public sealed class HealthMetricsCollector(HealthService health, string storagePath)
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastCpuAtUtc;

    public async Task<HealthSnapshotEntity> CaptureAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        _process.Refresh();
        var cpu = 0d;
        if (_lastCpuAtUtc != default)
        {
            var elapsed = (now - _lastCpuAtUtc).TotalSeconds;
            if (elapsed > 0) cpu = Math.Clamp((_process.TotalProcessorTime - _lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100d, 0, 100);
        }
        _lastCpu = _process.TotalProcessorTime;
        _lastCpuAtUtc = now;
        var memory = GC.GetGCMemoryInfo();
        var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(storagePath)) ?? Path.DirectorySeparatorChar.ToString());
        var gpu = await QueryGpuAsync(ct);
        var state = gpu.TotalBytes > 0 && gpu.UsedBytes * 100d / gpu.TotalBytes >= 95 ? "Warning" : "Healthy";
        return await health.RecordSnapshotAsync(new HealthSnapshotInput(
            state, cpu, (ulong)Math.Max(0, _process.WorkingSet64), (ulong)Math.Max(0, memory.TotalAvailableMemoryBytes),
            gpu.UsedBytes, gpu.TotalBytes, (ulong)Math.Max(0, disk.AvailableFreeSpace),
            new { processId = _process.Id, processMemoryBytes = _process.PrivateMemorySize64, gpu.Source }), ct);
    }

    private static async Task<GpuMemorySnapshot> QueryGpuAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return new GpuMemorySnapshot(0, 0, "unavailable");
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var values = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(',', StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length >= 2)
                .Select(parts => (Used: ulong.TryParse(parts[0], out var used) ? used : 0, Total: ulong.TryParse(parts[1], out var total) ? total : 0))
                .ToArray();
            if (values.Length == 0) return new GpuMemorySnapshot(0, 0, "unavailable");
            return new GpuMemorySnapshot((ulong)values.Sum(x => (decimal)x.Used) * 1024 * 1024, (ulong)values.Sum(x => (decimal)x.Total) * 1024 * 1024, "nvidia-smi");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return new GpuMemorySnapshot(0, 0, "unavailable");
        }
    }
}

public sealed class HealthMonitorService : IDisposable
{
    private readonly HealthMetricsCollector _collector;
    private readonly Timer _timer;
    private int _running;

    public HealthMonitorService(HealthMetricsCollector collector)
    {
        _collector = collector;
        _timer = new Timer(_ => _ = CaptureSafeAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public Task CaptureNowAsync(CancellationToken ct = default) => _collector.CaptureAsync(ct);

    private async Task CaptureSafeAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try { await _collector.CaptureAsync(); } catch { }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
