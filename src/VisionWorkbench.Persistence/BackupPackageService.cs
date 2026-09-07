using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

public sealed record BackupPackageResult(string Path, string Sha256, long SizeBytes, IReadOnlyList<string> Entries);

public sealed record BackupPackageManifest(
    int FormatVersion,
    string Product,
    string ProductVersion,
    DateTime CreatedAtUtc,
    string DatabaseEntry,
    IReadOnlyDictionary<string, string> FileHashes,
    string ChecksumsEntry = "checksums.sha256");

/// <summary>方案 §7：可验证的 .vwbackup 包，而不是只复制一个正在写入的 SQLite 文件。</summary>
public sealed class BackupPackageService
{
    private readonly VisionDbContextFactory _factory;
    private readonly string? _defaultSettingsPath;
    private readonly string? _defaultDataDirectory;

    public BackupPackageService(
        VisionDbContextFactory factory,
        string? defaultSettingsPath = null,
        string? defaultDataDirectory = null)
    {
        _factory = factory;
        _defaultSettingsPath = defaultSettingsPath;
        _defaultDataDirectory = defaultDataDirectory;
    }

    /// <summary>恢复前由宿主设置运行状态保护；生产、训练或通信运行时应返回 false。</summary>
    public Func<bool>? RestoreGuard { get; set; }

    /// <summary>恢复前创建当前状态备份，失败时恢复操作不会继续。</summary>
    public Func<CancellationToken, Task>? PreRestoreBackup { get; set; }
    public AuditService? AuditSink { get; set; }
    public async Task<BackupPackageResult> CreateAsync(
        string destinationPath,
        string? settingsPath = null,
        string? dataDirectory = null,
        string kind = "manual",
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var tempRoot = Path.Combine(Path.GetTempPath(), "VisionWorkbench-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var tempDb = Path.Combine(tempRoot, "visionworkbench.db");
        try
        {
            new DatabaseBackupService(_factory).BackupTo(tempDb);
            var files = new List<(string Entry, string Source)> { ("database/visionworkbench.db", tempDb) };
            settingsPath ??= _defaultSettingsPath;
            dataDirectory ??= _defaultDataDirectory;
            if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
            {
                files.Add(("settings/settings.json", Path.GetFullPath(settingsPath)));
            }
            if (!string.IsNullOrWhiteSpace(dataDirectory) && Directory.Exists(dataDirectory))
            {
                var root = Path.GetFullPath(dataDirectory);
                AddFiles("communication", Directory.EnumerateFiles(root, "*communication*.json", SearchOption.TopDirectoryOnly));
                // 新版可持久化参数统一位于 Config；保留相对目录，恢复后仍会回到 Config。
                var configDirectory = Path.Combine(root, "Config");
                if (Directory.Exists(configDirectory))
                {
                    var settingsFullPath = string.IsNullOrWhiteSpace(settingsPath)
                        ? null
                        : Path.GetFullPath(settingsPath);
                    foreach (var path in Directory.EnumerateFiles(configDirectory, "*", SearchOption.TopDirectoryOnly)
                                 .Where(path => !string.Equals(Path.GetFullPath(path), settingsFullPath, StringComparison.OrdinalIgnoreCase))
                                 .Where(path => !IsDatabaseFile(Path.GetFileName(path))))
                    {
                        files.Add(($"config/{Path.GetFileName(path)}", path));
                    }
                }
                AddFiles("recipes", Directory.EnumerateFiles(root, "*recipe*.json", SearchOption.AllDirectories));
                AddFiles("models", Directory.EnumerateFiles(root, "*.model.json", SearchOption.AllDirectories));
                AddFiles("plugins", Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories));
                // 只收集受支持的元数据，不把客户原图、缓存和临时文件悄悄塞进备份。
                var metadata = Directory.EnumerateFiles(dataDirectory, "*.manifest.json", SearchOption.AllDirectories)
                    .Where(path => !IsInside(path, tempRoot))
                    .Take(500);
                foreach (var path in metadata)
                {
                    var relative = Path.GetRelativePath(dataDirectory, path).Replace('\\', '/');
                    files.Add(($"metadata/{relative}", path));
                }

                void AddFiles(string folder, IEnumerable<string> candidates)
                {
                    foreach (var path in candidates.Where(File.Exists).Take(200))
                    {
                        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                        files.Add(($"{folder}/{relative}", path));
                    }
                }
            }

            var hashes = files.ToDictionary(item => item.Entry, item => ComputeSha256(item.Source), StringComparer.Ordinal);
            var manifest = new BackupPackageManifest(
                1,
                "VisionWorkbench",
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                DateTime.UtcNow,
                "database/visionworkbench.db",
                hashes);
            if (File.Exists(destination)) File.Delete(destination);
            using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    archive.CreateEntryFromFile(file.Source, file.Entry, CompressionLevel.Fastest);
                }
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using (var stream = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken);
                var checksums = archive.CreateEntry("checksums.sha256", CompressionLevel.Fastest);
                await using var checksumStream = new StreamWriter(checksums.Open());
                foreach (var item in hashes.OrderBy(x => x.Key, StringComparer.Ordinal))
                    await checksumStream.WriteLineAsync($"{item.Value}  {item.Key}");
            }
            var result = new BackupPackageResult(destination, ComputeSha256(destination), new FileInfo(destination).Length,
                files.Select(item => item.Entry).Append("manifest.json").Append("checksums.sha256").ToArray());
            await RecordAsync(result, kind, null, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await RecordAsync(new BackupPackageResult(destination, "", 0, []), kind, ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public async Task RestoreAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(packagePath);
        if (!File.Exists(source)) throw new FileNotFoundException("备份包不存在", source);
        var tempRoot = Path.Combine(Path.GetTempPath(), "VisionWorkbench-restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            BackupPackageManifest manifest;
            using (var archive = ZipFile.OpenRead(source))
            {
                foreach (var entry in archive.Entries) ValidateEntryName(entry.FullName);
                var manifestEntry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("备份包缺少 manifest.json");
                await using var stream = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync<BackupPackageManifest>(stream, cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException("备份清单无效");
                if (manifest.FormatVersion != 1 || !string.Equals(manifest.DatabaseEntry, "database/visionworkbench.db", StringComparison.Ordinal))
                    throw new InvalidDataException("不支持的备份包版本");

                foreach (var file in manifest.FileHashes)
                {
                    var entry = archive.GetEntry(file.Key) ?? throw new InvalidDataException($"备份包缺少 {file.Key}");
                    var extracted = Path.Combine(tempRoot, file.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
                    await using (var input = entry.Open())
                    await using (var output = File.Create(extracted))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                    }
                    if (!string.Equals(ComputeSha256(extracted), file.Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"备份条目校验失败：{file.Key}");
                }
            }
            using (var checksumArchive = ZipFile.OpenRead(source))
            {
                var checksumEntry = checksumArchive.GetEntry(manifest.ChecksumsEntry);
                if (checksumEntry is not null)
                {
                    using var checksumReader = new StreamReader(checksumEntry.Open());
                    var expected = manifest.FileHashes.ToDictionary(item => item.Key, item => $"{item.Value}  {item.Key}", StringComparer.Ordinal);
                    while (checksumReader.ReadLine() is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var separator = line.IndexOf("  ", StringComparison.Ordinal);
                        var entryName = separator > 0 ? line[(separator + 2)..] : "";
                        if (separator <= 0 || !expected.TryGetValue(entryName, out var expectedLine)
                            || !string.Equals(expectedLine, line, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("备份校验文件内容无效");
                        expected.Remove(entryName);
                    }
                    if (expected.Count != 0) throw new InvalidDataException("备份校验文件缺少条目");
                }
            }
            var database = Path.Combine(tempRoot, manifest.DatabaseEntry);
            ValidateSqliteFile(database);
            if (RestoreGuard is not null && !RestoreGuard())
                throw new InvalidOperationException("当前仍有生产、训练或通信任务运行，禁止恢复备份");
            if (PreRestoreBackup is not null)
                await PreRestoreBackup(cancellationToken);

            new DatabaseBackupService(_factory).RestoreFrom(database);
            await RestoreFilesAsync(tempRoot, manifest, cancellationToken);
            await RecordRestoreAuditAsync(source, "success", null, cancellationToken);
        }
        catch (Exception ex)
        {
            await RecordRestoreAuditAsync(source, "failed", ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private async Task RecordRestoreAuditAsync(string source, string result, string? error, CancellationToken ct)
    {
        if (AuditSink is null) return;
        try
        {
            await AuditSink.RecordAsync("backup.restore", "backup", source, "system", result,
                JsonSerializer.Serialize(new { error }), cancellationToken: ct);
        }
        catch { }
    }

    private async Task RestoreFilesAsync(string tempRoot, BackupPackageManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var entry in manifest.FileHashes.Keys.Where(x => !string.Equals(x, manifest.DatabaseEntry, StringComparison.OrdinalIgnoreCase)))
        {
            var source = Path.Combine(tempRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            string? destination = null;
            if (string.Equals(entry, "settings/settings.json", StringComparison.OrdinalIgnoreCase))
            {
                destination = _defaultSettingsPath;
            }
            else if (!string.IsNullOrWhiteSpace(_defaultDataDirectory))
            {
                var slash = entry.IndexOf('/');
                if (slash > 0)
                {
                    var relative = entry[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
                    destination = Path.Combine(_defaultDataDirectory, relative);
                }
            }
            if (string.IsNullOrWhiteSpace(destination)) continue;
            var fullDestination = Path.GetFullPath(destination);
            var root = Path.GetFullPath(_defaultDataDirectory ?? Path.GetDirectoryName(fullDestination) ?? AppContext.BaseDirectory);
            if (!IsInside(fullDestination, root) && !string.Equals(fullDestination, Path.GetFullPath(_defaultSettingsPath ?? ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("恢复目标路径不安全");
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(fullDestination);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    public static string ComputeSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private async Task RecordAsync(BackupPackageResult result, string kind, string? error, CancellationToken cancellationToken)
    {
        await using var db = _factory.CreateDbContext();
        db.BackupRecords.Add(new BackupRecordEntity
        {
            Path = result.Path,
            Kind = kind,
            Status = error is null ? "success" : "failed",
            Sha256 = string.IsNullOrWhiteSpace(result.Sha256) ? null : result.Sha256,
            SizeBytes = result.SizeBytes,
            Error = error,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsInside(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsDatabaseFile(string fileName) =>
        string.Equals(fileName, "visionworkbench.db", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "visionworkbench.db-wal", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "visionworkbench.db-shm", StringComparison.OrdinalIgnoreCase);

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains('\\')
            || name.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"备份包包含不安全路径：{name}");
    }

    private static void ValidateSqliteFile(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("备份包中的数据库不是有效 SQLite 文件");
    }
}
