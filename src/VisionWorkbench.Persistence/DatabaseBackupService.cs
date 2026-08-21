using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

/// <summary>SQLite 备份/恢复（DAT-007）：备份使用 VACUUM INTO，包含 WAL 中已提交数据。</summary>
public sealed class DatabaseBackupService(VisionDbContextFactory factory)
{
    public string DatabasePath => factory.DbPath;

    public void BackupTo(string destinationPath)
    {
        var destination = NormalizePath(destinationPath);
        EnsureSqlitePathIsNotSame(destination);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        using var db = factory.CreateDbContext();
        db.Database.ExecuteSqlInterpolated($"VACUUM INTO {destination}");
        ValidateSqliteFile(destination);
    }

    public void RestoreFrom(string sourcePath)
    {
        var source = NormalizePath(sourcePath);
        ValidateSqliteFile(source);
        var database = NormalizePath(factory.DbPath);
        EnsureSqlitePathIsNotSame(source, database);

        // 复制到临时文件后替换，避免恢复过程中留下半个数据库。
        var temp = database + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temp, overwrite: true);

            // Ensure pooled SQLite connections do not keep the pre-restore file open.
            using (var db = factory.CreateDbContext())
            {
                db.Database.CloseConnection();
            }
            SqliteConnection.ClearAllPools();

            DeleteSidecar(database + "-wal");
            DeleteSidecar(database + "-shm");
            File.Replace(temp, database, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            DeleteSidecar(temp);
        }
    }

    private void EnsureSqlitePathIsNotSame(string path) => EnsureSqlitePathIsNotSame(path, factory.DbPath);

    private static void EnsureSqlitePathIsNotSame(string left, string right)
    {
        if (string.Equals(left, NormalizePath(right), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("备份/恢复路径不能与当前数据库相同");
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static void ValidateSqliteFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("SQLite 文件不存在", path);
        }
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header) != header.Length
            || !header.SequenceEqual("SQLite format 3\0"u8))
        {
            throw new InvalidDataException("文件不是有效的 SQLite 数据库");
        }
    }

    private static void DeleteSidecar(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 下次启动时仍可再次清理；不覆盖主操作异常。
        }
    }
}
