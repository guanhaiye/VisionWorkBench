using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

/// <summary>SQLite + EF Core（文档 §20）。开发期 EnsureCreated，零迁移成本。</summary>
public sealed class VisionDbContext(DbContextOptions<VisionDbContext> options) : DbContext(options)
{
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<BatchEntity> Batches => Set<BatchEntity>();
    public DbSet<InspectionRecordEntity> InspectionRecords => Set<InspectionRecordEntity>();
    public DbSet<CountingEventEntity> CountingEvents => Set<CountingEventEntity>();
    public DbSet<VisionEventEntity> VisionEvents => Set<VisionEventEntity>();
    public DbSet<CorrectionEntity> Corrections => Set<CorrectionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskEntity>(e =>
        {
            e.ToTable("Tasks");
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<BatchEntity>(e =>
        {
            e.ToTable("Batches");
            e.HasIndex(x => new { x.TaskId, x.BatchNumber }).IsUnique();
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId);
        });

        modelBuilder.Entity<InspectionRecordEntity>(e =>
        {
            e.ToTable("InspectionRecords");
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => new { x.TaskId, x.StartedAt });
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId);
            e.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId);
        });

        modelBuilder.Entity<CountingEventEntity>(e =>
        {
            e.ToTable("CountingEvents");
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.CounterId);
            e.HasIndex(x => x.OccurredAt);
            e.HasOne(x => x.Record).WithMany().HasForeignKey(x => x.RecordId);
            e.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId);
        });

        modelBuilder.Entity<VisionEventEntity>(e =>
        {
            e.ToTable("VisionEvents");
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.EventType);
            e.HasOne(x => x.Record).WithMany().HasForeignKey(x => x.RecordId);
            e.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId);
        });

        modelBuilder.Entity<CorrectionEntity>(e =>
        {
            e.ToTable("Corrections");
            e.HasIndex(x => x.RecordId);
            e.HasIndex(x => x.CorrectedAt);
            e.HasOne(x => x.Record).WithMany().HasForeignKey(x => x.RecordId);
        });
    }
}

/// <summary>
/// Context 工厂：EF Context 非线程安全，仓储每次操作用短生命周期 Context
/// （后台落库线程与 UI 查询线程并发访问安全）。
/// </summary>
public sealed class VisionDbContextFactory(string dbPath) : IDbContextFactory<VisionDbContext>
{
    public string DbPath { get; } = dbPath;

    public VisionDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<VisionDbContext>()
            .UseSqlite($"Data Source={DbPath}")
            .Options;
        return new VisionDbContext(options);
    }

    /// <summary>建目录 + 建库（幂等）+ 开启 WAL（读写并发，文档 §20），返回工厂。</summary>
    public static VisionDbContextFactory Create(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        using var db = new VisionDbContextFactory(dbPath).CreateDbContext();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return new VisionDbContextFactory(dbPath);
    }
}
