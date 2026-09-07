using System.Globalization;
using System.Security.Cryptography;

namespace VisionWorkbench.Persistence;

public sealed record BackupRetentionResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Kept);

/// <summary>按日/周保留备份；永远不删除最近一个可验证备份。</summary>
public sealed class BackupRetentionService
{
    public BackupRetentionResult Apply(string directory, int daily = 7, int weekly = 4)
    {
        if (!Directory.Exists(directory)) return new BackupRetentionResult([], []);
        var files = Directory.EnumerateFiles(directory, "*.vwbackup", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path)).OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Take(Math.Max(1, daily))) keep.Add(file.FullName);
        foreach (var file in files.GroupBy(x => ISOWeek.GetYear(x.LastWriteTimeUtc) + "-" + ISOWeek.GetWeekOfYear(x.LastWriteTimeUtc)).Take(Math.Max(1, weekly)).Select(g => g.First())) keep.Add(file.FullName);
        if (files.Count > 0) keep.Add(files[0].FullName);
        var deleted = new List<string>();
        foreach (var file in files.Where(x => !keep.Contains(x.FullName)))
        {
            try { File.Delete(file.FullName); deleted.Add(file.FullName); } catch (IOException) { }
        }
        return new BackupRetentionResult(deleted, keep.OrderBy(x => x).ToArray());
    }

    /// <summary>按版本数量保留自动备份，始终保留最新版本。</summary>
    public BackupRetentionResult Apply(string directory, int maxCount)
    {
        if (!Directory.Exists(directory)) return new BackupRetentionResult([], []);
        var files = Directory.EnumerateFiles(directory, "*.vwbackup", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .ToList();
        var keep = files.Take(Math.Max(1, maxCount)).Select(x => x.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        foreach (var file in files.Where(x => !keep.Contains(x.FullName)))
        {
            try { File.Delete(file.FullName); deleted.Add(file.FullName); } catch (IOException) { }
        }
        return new BackupRetentionResult(deleted, keep.OrderBy(x => x).ToArray());
    }
}

public sealed class AutomaticBackupService : IDisposable
{
    private readonly BackupPackageService _packages;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly string _backupDirectory;
    private readonly BackupRetentionService _retention = new();
    private readonly Timer _timer;
    private readonly object _scheduleGate = new();
    private bool _enabled;
    private int _intervalMinutes;
    private int _retentionCount;
    private int _running;

    public AutomaticBackupService(
        BackupPackageService packages,
        string settingsPath,
        string dataDirectory,
        bool enabled = true,
        int intervalMinutes = 1440,
        int retentionCount = 30)
    {
        _packages = packages; _settingsPath = settingsPath; _dataDirectory = dataDirectory; _backupDirectory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(_backupDirectory);
        _timer = new Timer(_ => _ = RunScheduledAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Configure(enabled, intervalMinutes, retentionCount);
    }

    public string BackupDirectory => _backupDirectory;

    public async Task RunNowAsync(CancellationToken ct = default) => await RunCoreAsync(ct);

    public void Configure(bool enabled, int intervalMinutes, int retentionCount)
    {
        lock (_scheduleGate)
        {
            _enabled = enabled;
            _intervalMinutes = Math.Clamp(intervalMinutes, 1440, 43200);
            _retentionCount = Math.Clamp(retentionCount, 1, 365);
            _timer.Change(
                _enabled ? TimeSpan.FromMinutes(_intervalMinutes) : Timeout.InfiniteTimeSpan,
                _enabled ? TimeSpan.FromMinutes(_intervalMinutes) : Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunScheduledAsync()
    {
        lock (_scheduleGate)
        {
            if (!_enabled) return;
        }
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try { await RunCoreAsync(CancellationToken.None); } catch { } finally { Volatile.Write(ref _running, 0); }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_backupDirectory);
        var path = Path.Combine(_backupDirectory, $"automatic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.vwbackup");
        await _packages.CreateAsync(path, _settingsPath, _dataDirectory, "automatic", ct);
        int retentionCount;
        lock (_scheduleGate) retentionCount = _retentionCount;
        _retention.Apply(_backupDirectory, retentionCount);
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>可选的 AES-256-GCM 外层加密，密钥由调用方保管，不写入备份包。</summary>
public sealed class EncryptedBackupPackageService(BackupPackageService packages)
{
    private static readonly byte[] Magic = "VWBKENC1"u8.ToArray();

    public async Task EncryptAsync(string sourcePackage, string destination, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ValidateKey(key.Span); var plain = await File.ReadAllBytesAsync(sourcePackage, ct); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key.Span, 16); aes.Encrypt(nonce, plain, cipher, tag);
        await using var output = File.Create(destination); await output.WriteAsync(Magic, ct); await output.WriteAsync(nonce, ct); await output.WriteAsync(tag, ct); await output.WriteAsync(cipher, ct);
    }

    public async Task RestoreAsync(string encryptedPackage, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ValidateKey(key.Span); var bytes = await File.ReadAllBytesAsync(encryptedPackage, ct);
        if (bytes.Length < Magic.Length + 12 + 16 || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("加密备份格式无效");
        var nonce = bytes.AsSpan(Magic.Length, 12); var tag = bytes.AsSpan(Magic.Length + 12, 16); var cipher = bytes.AsSpan(Magic.Length + 28); var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key.Span, 16); aes.Decrypt(nonce, cipher, tag, plain);
        var temp = Path.Combine(Path.GetTempPath(), "VisionWorkbench-" + Guid.NewGuid().ToString("N") + ".vwbackup");
        try { await File.WriteAllBytesAsync(temp, plain, ct); await packages.RestoreAsync(temp, ct); } finally { try { File.Delete(temp); } catch { } }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key) { if (key.Length != 32) throw new ArgumentException("AES-256-GCM 密钥必须为 32 字节", nameof(key)); }
}
