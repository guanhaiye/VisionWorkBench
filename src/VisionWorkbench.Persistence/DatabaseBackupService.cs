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
        EnsureSqlitePathIsNotSame(destination, factory.DbPath);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var temp = destination + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            using var db = factory.CreateDbContext();
            db.Database.ExecuteSqlInterpolated($"VACUUM INTO {temp}");
            ValidateSqliteFile(temp);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temp);
        }
    }

    public void RestoreFrom(string sourcePath)
    {
        var source = NormalizePath(sourcePath);
        ValidateSqliteFile(source);
        var database = NormalizePath(factory.DbPath);
        EnsureSqlitePathIsNotSame(source, database);

        // SQLite's backup API uses a destination transaction and respects active WAL
        // connections. Deleting WAL before replacing a file can lose committed data.
        using var input = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        input.Open();
        output.Open();
        input.BackupDatabase(output);
    }

    private static void EnsureSqlitePathIsNotSame(string left, string right)
    {
        if (string.Equals(left, NormalizePath(right), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("备份/恢复路径不能与当前数据库相同");
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static void ValidateSqliteFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("SQLite 文件不存在", path);
        using (var stream = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[16];
            if (stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidDataException("文件不是有效的 SQLite 数据库");
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite 数据库完整性校验失败");
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}