using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

/// <summary>SQLite + EF Core（文档 §20）。开发期 EnsureCreated，零迁移成本。</summary>
public sealed class VisionDbContext(DbContextOptions<VisionDbContext> options) : DbContext(options)
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<StationEntity> Stations => Set<StationEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<BatchEntity> Batches => Set<BatchEntity>();
    public DbSet<InspectionRecordEntity> InspectionRecords => Set<InspectionRecordEntity>();
    public DbSet<CountingEventEntity> CountingEvents => Set<CountingEventEntity>();
    public DbSet<VisionEventEntity> VisionEvents => Set<VisionEventEntity>();
    public DbSet<CorrectionEntity> Corrections => Set<CorrectionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEntity>(e =>
        {
            e.ToTable("Projects");
            e.Property(x => x.ProjectCode).UseCollation("NOCASE");
            e.HasIndex(x => x.ProjectCode).IsUnique();
        });

        modelBuilder.Entity<StationEntity>(e =>
        {
            e.ToTable("Stations");
            e.Property(x => x.StationCode).UseCollation("NOCASE");
            e.HasIndex(x => new { x.ProjectId, x.StationCode }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.IsArchived });
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId);
        });

        modelBuilder.Entity<TaskEntity>(e =>
        {
            e.ToTable("Tasks");
            e.HasIndex(x => x.Name).IsUnique();
            // 工位编号的唯一范围属于 Stations(ProjectId, StationCode)，任务本身可被多个工位复用。
            e.HasIndex(x => x.StationCode);
        });

        modelBuilder.Entity<BatchEntity>(e =>
        {
            e.ToTable("Batches");
            e.HasIndex(x => new { x.TaskId, x.BatchNumber }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.StationCode });
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId);
        });

        modelBuilder.Entity<InspectionRecordEntity>(e =>
        {
            e.ToTable("InspectionRecords");
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => new { x.ProjectId, x.StationCode });
            e.HasIndex(x => x.StationCode);
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.SourceRecordId);
            e.HasIndex(x => new { x.RunType, x.StartedAt });
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
        UpgradeSchema(db);
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return new VisionDbContextFactory(dbPath);
    }

    private static void UpgradeSchema(VisionDbContext db)
    {
        // EnsureCreated 不会修改已有数据库；这里为旧版单工位数据库做幂等升级。
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Projects (Id INTEGER NOT NULL CONSTRAINT PK_Projects PRIMARY KEY AUTOINCREMENT, ProjectCode TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Projects_ProjectCode ON Projects (ProjectCode);");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Stations (Id INTEGER NOT NULL CONSTRAINT PK_Stations PRIMARY KEY AUTOINCREMENT, ProjectId INTEGER NOT NULL, StationCode TEXT NOT NULL, Name TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1, IsArchived INTEGER NOT NULL DEFAULT 0, TaskId INTEGER NULL, CameraProviderId TEXT NULL, CameraDeviceId TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, ArchivedAt TEXT NULL, FOREIGN KEY (ProjectId) REFERENCES Projects (Id));");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Stations_ProjectId_StationCode ON Stations (ProjectId, StationCode);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Stations_ProjectId_IsArchived ON Stations (ProjectId, IsArchived);");

        var projectCount = Convert.ToInt64(db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM Projects").Single());
        if (projectCount == 0)
        {
            db.Database.ExecuteSqlRaw("INSERT INTO Projects (ProjectCode, Name, Description, CreatedAt, UpdatedAt) VALUES ('default', '默认项目', '由旧版单工位数据自动创建', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);");
        }

        var taskColumns = ReadColumns(db, "Tasks");
        if (!taskColumns.Contains("StationCode"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Tasks ADD COLUMN StationCode TEXT NOT NULL DEFAULT '';");
            db.Database.ExecuteSqlRaw("UPDATE Tasks SET StationCode = printf('ST-%03d', Id) WHERE StationCode = '';");
        }

        var recordColumns = ReadColumns(db, "InspectionRecords");
        if (!recordColumns.Contains("StationCode"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecords ADD COLUMN StationCode TEXT NOT NULL DEFAULT '';");
            db.Database.ExecuteSqlRaw("UPDATE InspectionRecords SET StationCode = COALESCE((SELECT StationCode FROM Tasks WHERE Tasks.Id = InspectionRecords.TaskId), printf('ST-%03d', TaskId)) WHERE StationCode = '';");
        }

        db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Tasks_StationCode;");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Tasks_StationCode ON Tasks (StationCode);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InspectionRecords_StationCode ON InspectionRecords (StationCode);");

        var defaultProjectId = Convert.ToInt64(db.Database.SqlQueryRaw<long>("SELECT Id AS Value FROM Projects WHERE ProjectCode = 'default' LIMIT 1").Single());
        db.Database.ExecuteSqlRaw("INSERT INTO Stations (ProjectId, StationCode, Name, Enabled, IsArchived, TaskId, CameraProviderId, CameraDeviceId, CreatedAt, UpdatedAt) SELECT @p0, t.StationCode, t.Name, 1, 0, t.Id, t.CameraProviderId, t.CameraDeviceId, t.CreatedAt, t.UpdatedAt FROM Tasks t WHERE NOT EXISTS (SELECT 1 FROM Stations s WHERE s.TaskId = t.Id) AND NOT EXISTS (SELECT 1 FROM Stations s WHERE s.ProjectId = @p0 AND lower(s.StationCode) = lower(t.StationCode));", defaultProjectId);

        var batchColumns = ReadColumns(db, "Batches");
        if (!batchColumns.Contains("ProjectId"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Batches ADD COLUMN ProjectId TEXT NOT NULL DEFAULT 'default';");
        }
        if (!batchColumns.Contains("StationCode"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Batches ADD COLUMN StationCode TEXT NOT NULL DEFAULT '';");
            db.Database.ExecuteSqlRaw("UPDATE Batches SET StationCode = COALESCE((SELECT StationCode FROM Tasks WHERE Tasks.Id = Batches.TaskId), printf('ST-%03d', TaskId)) WHERE StationCode = '';");
        }

        var updatedRecordColumns = ReadColumns(db, "InspectionRecords");
        if (!updatedRecordColumns.Contains("ProjectId"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecords ADD COLUMN ProjectId TEXT NOT NULL DEFAULT 'default';");
        }
        if (!updatedRecordColumns.Contains("SourceRecordId"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecords ADD COLUMN SourceRecordId INTEGER NULL;");
        }
        if (!updatedRecordColumns.Contains("RunType"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecords ADD COLUMN RunType TEXT NOT NULL DEFAULT 'production';");
        }
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InspectionRecords_SourceRecordId ON InspectionRecords (SourceRecordId);");
    }

    private static HashSet<string> ReadColumns(VisionDbContext db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            connection.Close();
        }
        return columns;
    }
}
