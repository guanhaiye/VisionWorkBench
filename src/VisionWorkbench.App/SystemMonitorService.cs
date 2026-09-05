using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace VisionWorkbench.App;

public sealed record DiskMonitorItem(string Name, ulong UsedBytes, ulong TotalBytes)
{
    public double UsagePercent => TotalBytes == 0 ? 0 : Math.Clamp(UsedBytes * 100d / TotalBytes, 0, 100);
    public string UsageText => $"{UsagePercent:0.0}%";
    public string CapacityText => $"{ByteSizeFormatter.Format(UsedBytes)} / {ByteSizeFormatter.Format(TotalBytes)}";
}

public sealed record SystemMonitorSnapshot(
    double CpuUsage,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    string GpuName,
    ulong GpuUsedBytes,
    ulong GpuTotalBytes,
    double? GpuUsage,
    IReadOnlyList<DiskMonitorItem> Disks);

public sealed class SystemMonitorService
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasPreviousCpu;

    public SystemMonitorSnapshot Read()
    {
        var cpu = ReadCpuUsage();
        var memory = ReadMemory();
        var gpu = ReadGpu();
        var disks = ReadDisks();
        return new SystemMonitorSnapshot(
            cpu,
            memory.used,
            memory.total,
            gpu.name,
            gpu.used,
            gpu.total,
            gpu.usage,
            disks);
    }

    private double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return 0;

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        if (!_hasPreviousCpu)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _hasPreviousCpu = true;
            return 0;
        }

        var idleDelta = idle - _previousIdle;
        var totalDelta = (kernel - _previousKernel) + (user - _previousUser);
        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;
        if (totalDelta == 0) return 0;
        return Math.Clamp((1d - (double)idleDelta / totalDelta) * 100d, 0, 100);
    }

    private static (ulong used, ulong total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return (status.TotalPhysicalMemory - status.AvailablePhysicalMemory, status.TotalPhysicalMemory);
    }

    private static (string name, ulong used, ulong total, double? usage) ReadGpu()
    {
        var nvidia = RunCommand("nvidia-smi", "--query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits");
        if (!string.IsNullOrWhiteSpace(nvidia))
        {
            var fields = nvidia.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length >= 4
                && ulong.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedMb)
                && ulong.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMb))
            {
                double? usage = double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var gpuUsage)
                    ? gpuUsage
                    : null;
                return (fields[0], usedMb * 1024 * 1024, totalMb * 1024 * 1024, usage);
            }
        }

        // Windows performance counters cover integrated GPUs and non-NVIDIA adapters when available.
        const string script = "$u=(Get-Counter '\\GPU Adapter Memory(*)\\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | Measure-Object CookedValue -Sum; $l=(Get-Counter '\\GPU Adapter Memory(*)\\Dedicated Limit' -ErrorAction SilentlyContinue).CounterSamples | Measure-Object CookedValue -Sum; if ($l.Sum -gt 0) { '{0},{1}' -f [UInt64]$u.Sum,[UInt64]$l.Sum }";
        var counters = RunCommand("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"");
        var counterFields = counters?.Split(',', StringSplitOptions.TrimEntries);
        if (counterFields?.Length == 2
            && ulong.TryParse(counterFields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var counterUsed)
            && ulong.TryParse(counterFields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var counterTotal)
            && counterTotal > 0)
        {
            return ("Windows GPU", counterUsed, counterTotal, null);
        }

        return ("未检测到可用显存接口", 0, 0, null);
    }

    private static IReadOnlyList<DiskMonitorItem> ReadDisks()
    {
        var disks = new List<DiskMonitorItem>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(item => item.IsReady))
            {
                disks.Add(new DiskMonitorItem(
                    drive.Name.TrimEnd('\\'),
                    (ulong)(drive.TotalSize - drive.AvailableFreeSpace),
                    (ulong)drive.TotalSize));
            }
        }
        catch
        {
        }

        return disks.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1200))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            return output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
