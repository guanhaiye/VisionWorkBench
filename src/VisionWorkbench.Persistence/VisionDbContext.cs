using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

/// <summary>SQLite + EF Core。保留旧库幂等升级，同时为商用版提供可追踪 schema 版本。</summary>
public sealed class VisionDbContext(DbContextOptions<VisionDbContext> options) : DbContext(options)
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<StationEntity> Stations => Set<StationEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<SopRunEntity> SopRuns => Set<SopRunEntity>();
    public DbSet<SopStepResultEntity> SopStepResults => Set<SopStepResultEntity>();
    public DbSet<BatchEntity> Batches => Set<BatchEntity>();
    public DbSet<InspectionRecordEntity> InspectionRecords => Set<InspectionRecordEntity>();
    public DbSet<CountingEventEntity> CountingEvents => Set<CountingEventEntity>();
    public DbSet<VisionEventEntity> VisionEvents => Set<VisionEventEntity>();
    public DbSet<CorrectionEntity> Corrections => Set<CorrectionEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<PermissionEntity> Permissions => Set<PermissionEntity>();
    public DbSet<UserRoleEntity> UserRoles => Set<UserRoleEntity>();
    public DbSet<RolePermissionEntity> RolePermissions => Set<RolePermissionEntity>();
    public DbSet<LoginEventEntity> LoginEvents => Set<LoginEventEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<BackupRecordEntity> BackupRecords => Set<BackupRecordEntity>();
    public DbSet<HealthSnapshotEntity> HealthSnapshots => Set<HealthSnapshotEntity>();
    public DbSet<AlertEntity> Alerts => Set<AlertEntity>();
    public DbSet<CommunicationRequestEntity> CommunicationRequests => Set<CommunicationRequestEntity>();
    public DbSet<RecipeVersionEntity> RecipeVersions => Set<RecipeVersionEntity>();
    public DbSet<ModelArtifactEntity> ModelArtifacts => Set<ModelArtifactEntity>();
    public DbSet<DeploymentBindingEntity> DeploymentBindings => Set<DeploymentBindingEntity>();
    public DbSet<LicenseEventEntity> LicenseEvents => Set<LicenseEventEntity>();
    public DbSet<DatasetVersionEntity> DatasetVersions => Set<DatasetVersionEntity>();
    public DbSet<ReportJobEntity> ReportJobs => Set<ReportJobEntity>();

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

        modelBuilder.Entity<SopRunEntity>(e =>
        {
            e.ToTable("SopRuns");
            e.HasIndex(x => new { x.ProjectId, x.StationCode, x.CycleId }).IsUnique();
            e.HasIndex(x => new { x.BatchId, x.StartedAtUtc });
            e.HasIndex(x => new { x.Status, x.StartedAtUtc });
        });

        modelBuilder.Entity<SopStepResultEntity>(e =>
        {
            e.ToTable("SopStepResults");
            e.HasIndex(x => new { x.SopRunId, x.StepId, x.Attempt }).IsUnique();
            e.HasOne(x => x.SopRun).WithMany().HasForeignKey(x => x.SopRunId);
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
            e.HasIndex(x => x.SopRunId);
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
            e.HasIndex(x => x.SopRunId);
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

        modelBuilder.Entity<UserEntity>(e => { e.ToTable("Users"); e.HasIndex(x => x.UserName).IsUnique(); });
        modelBuilder.Entity<RoleEntity>(e => { e.ToTable("Roles"); e.HasIndex(x => x.Code).IsUnique(); });
        modelBuilder.Entity<PermissionEntity>(e => { e.ToTable("Permissions"); e.HasIndex(x => x.Code).IsUnique(); });
        modelBuilder.Entity<UserRoleEntity>(e => { e.ToTable("UserRoles"); e.HasKey(x => new { x.UserId, x.RoleId }); });
        modelBuilder.Entity<RolePermissionEntity>(e => { e.ToTable("RolePermissions"); e.HasKey(x => new { x.RoleId, x.PermissionId }); });
        modelBuilder.Entity<LoginEventEntity>(e => { e.ToTable("LoginEvents"); e.HasIndex(x => x.OccurredAtUtc); e.HasIndex(x => x.UserName); });
        modelBuilder.Entity<AuditEventEntity>(e => { e.ToTable("AuditEvents"); e.HasIndex(x => x.OccurredAtUtc); e.HasIndex(x => new { x.ObjectType, x.ObjectId }); e.HasIndex(x => x.Hash); });
        modelBuilder.Entity<BackupRecordEntity>(e => { e.ToTable("BackupRecords"); e.HasIndex(x => x.CreatedAtUtc); });
        modelBuilder.Entity<HealthSnapshotEntity>(e => { e.ToTable("HealthSnapshots"); e.HasIndex(x => x.CapturedAtUtc); });
        modelBuilder.Entity<AlertEntity>(e => { e.ToTable("Alerts"); e.HasIndex(x => x.Code).HasDatabaseName("IX_Alerts_ActiveCode").IsUnique().HasFilter("Status = 'active'"); e.HasIndex(x => x.LastSeenAtUtc); });
        modelBuilder.Entity<CommunicationRequestEntity>(e =>
        {
            e.ToTable("CommunicationRequests");
            e.HasIndex(x => new { x.ProjectCode, x.ClientId, x.RequestId }).IsUnique();
            e.HasIndex(x => x.ExpiresAtUtc);
        });
        modelBuilder.Entity<RecipeVersionEntity>(e => { e.ToTable("RecipeVersions"); e.HasIndex(x => new { x.RecipeCode, x.Version }).IsUnique(); e.HasIndex(x => new { x.RecipeCode, x.State }); });
        modelBuilder.Entity<ModelArtifactEntity>(e => { e.ToTable("ModelArtifacts"); e.HasIndex(x => new { x.ModelCode, x.Version }).IsUnique(); e.HasIndex(x => x.Sha256); });
        modelBuilder.Entity<DeploymentBindingEntity>(e => { e.ToTable("DeploymentBindings"); e.HasIndex(x => x.TargetCode).IsUnique(); });
        modelBuilder.Entity<LicenseEventEntity>(e => { e.ToTable("LicenseEvents"); e.HasIndex(x => x.OccurredAtUtc); });
        modelBuilder.Entity<DatasetVersionEntity>(e => { e.ToTable("DatasetVersions"); e.HasIndex(x => new { x.DatasetCode, x.Version }).IsUnique(); e.HasIndex(x => x.Sha256); });
        modelBuilder.Entity<ReportJobEntity>(e => { e.ToTable("ReportJobs"); e.HasIndex(x => x.CreatedAtUtc); });
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
            .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DbPath }.ToString())
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
        // EnsureCreated 只适用于全新数据库。旧数据库如果缺少后来新增的表，
        // EnsureCreated 会尝试重放整套模型建表语句，可能与已有索引重名而启动失败。
        // 已存在用户表时直接执行下面的幂等升级脚本，避免重建旧模型并保留业务数据。
        if (!HasUserTables(db))
        {
            db.Database.EnsureCreated();
        }
        UpgradeSchema(db);
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return new VisionDbContextFactory(dbPath);
    }

    private static bool HasUserTables(VisionDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%');";
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }
        finally
        {
            connection.Close();
        }
    }

    private static void UpgradeSchema(VisionDbContext db)
    {
        // EnsureCreated 不会修改已有数据库；这里为旧版单工位数据库做幂等升级。
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Projects (Id INTEGER NOT NULL CONSTRAINT PK_Projects PRIMARY KEY AUTOINCREMENT, ProjectCode TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Projects_ProjectCode ON Projects (ProjectCode);");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Stations (Id INTEGER NOT NULL CONSTRAINT PK_Stations PRIMARY KEY AUTOINCREMENT, ProjectId INTEGER NOT NULL, StationCode TEXT NOT NULL, Name TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1, IsArchived INTEGER NOT NULL DEFAULT 0, TaskId INTEGER NULL, CameraProviderId TEXT NULL, CameraDeviceId TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, ArchivedAt TEXT NULL, FOREIGN KEY (ProjectId) REFERENCES Projects (Id));");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Stations_ProjectId_StationCode ON Stations (ProjectId, StationCode);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Stations_ProjectId_IsArchived ON Stations (ProjectId, IsArchived);");

        // SOP 产品周期表：仅新增结构，不影响旧任务和旧记录。
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS SopRuns (
                Id INTEGER NOT NULL CONSTRAINT PK_SopRuns PRIMARY KEY AUTOINCREMENT,
                SopDefinitionId TEXT NOT NULL,
                SopVersion INTEGER NOT NULL DEFAULT 1,
                DefinitionHash TEXT NOT NULL,
                DefinitionSnapshotJson TEXT NOT NULL,
                ProjectId TEXT NOT NULL DEFAULT 'default',
                StationCode TEXT NOT NULL,
                TaskId INTEGER NOT NULL,
                BatchId INTEGER NULL,
                CycleId TEXT NOT NULL,
                ProductId TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'Idle',
                CurrentStepOrder INTEGER NOT NULL DEFAULT 0,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FailureReason TEXT NULL,
                FinalStatus TEXT NULL,
                FinalDecisionJson TEXT NULL,
                FinalResultJson TEXT NULL,
                FinalizedAtUtc TEXT NULL,
                FinalPublishedAtUtc TEXT NULL,
                FinalPublishClaimId TEXT NULL,
                FinalPublishClaimedAtUtc TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SopRuns_ProjectId_StationCode_CycleId ON SopRuns(ProjectId, StationCode, CycleId);
            CREATE INDEX IF NOT EXISTS IX_SopRuns_BatchId_StartedAtUtc ON SopRuns(BatchId, StartedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_SopRuns_Status_StartedAtUtc ON SopRuns(Status, StartedAtUtc);
            CREATE TABLE IF NOT EXISTS SopStepResults (
                Id INTEGER NOT NULL CONSTRAINT PK_SopStepResults PRIMARY KEY AUTOINCREMENT,
                SopRunId INTEGER NOT NULL,
                StepId TEXT NOT NULL,
                Attempt INTEGER NOT NULL DEFAULT 1,
                Status TEXT NOT NULL DEFAULT 'Pending',
                StartedAtUtc TEXT NULL,
                CompletedAtUtc TEXT NULL,
                Confidence REAL NULL,
                ConditionResultJson TEXT NULL,
                FailureReason TEXT NULL,
                InspectionRecordId INTEGER NULL,
                FOREIGN KEY(SopRunId) REFERENCES SopRuns(Id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SopStepResults_SopRunId_StepId_Attempt ON SopStepResults(SopRunId, StepId, Attempt);
            """);

        var sopRunColumns = ReadColumns(db, "SopRuns");
        if (!sopRunColumns.Contains("FinalStatus")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalStatus TEXT NULL;");
        if (!sopRunColumns.Contains("FinalDecisionJson")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalDecisionJson TEXT NULL;");
        if (!sopRunColumns.Contains("FinalResultJson")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalResultJson TEXT NULL;");
        if (!sopRunColumns.Contains("FinalizedAtUtc")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalizedAtUtc TEXT NULL;");
        if (!sopRunColumns.Contains("FinalPublishedAtUtc")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalPublishedAtUtc TEXT NULL;");
        if (!sopRunColumns.Contains("FinalPublishClaimId")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalPublishClaimId TEXT NULL;");
        if (!sopRunColumns.Contains("FinalPublishClaimedAtUtc")) db.Database.ExecuteSqlRaw("ALTER TABLE SopRuns ADD COLUMN FinalPublishClaimedAtUtc TEXT NULL;");

        var projectCount = Convert.ToInt64(db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM Projects").AsEnumerable().Single());
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
        if (!taskColumns.Contains("WorkflowJson"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Tasks ADD COLUMN WorkflowJson TEXT NULL;");
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

        var defaultProjectId = Convert.ToInt64(db.Database.SqlQueryRaw<long>("SELECT Id AS Value FROM Projects WHERE ProjectCode = 'default' LIMIT 1").AsEnumerable().Single());
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
        if (!updatedRecordColumns.Contains("WorkflowResultJson"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecords ADD COLUMN WorkflowResultJson TEXT NULL;");
        }
        if (!updatedRecordColumns.Contains("SopRunId"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecords ADD COLUMN SopRunId INTEGER NULL;");
        }
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InspectionRecords_SopRunId ON InspectionRecords (SopRunId);");

        var visionEventColumns = ReadColumns(db, "VisionEvents");
        if (!visionEventColumns.Contains("SopRunId")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN SopRunId INTEGER NULL;");
        if (!visionEventColumns.Contains("SourceTaskId")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN SourceTaskId INTEGER NULL;");
        if (!visionEventColumns.Contains("SourceStationCode")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN SourceStationCode TEXT NULL;");
        if (!visionEventColumns.Contains("FrameSequence")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN FrameSequence INTEGER NULL;");
        if (!visionEventColumns.Contains("BoxJson")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN BoxJson TEXT NULL;");
        if (!visionEventColumns.Contains("TextValue")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN TextValue TEXT NULL;");
        if (!visionEventColumns.Contains("CodeValue")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN CodeValue TEXT NULL;");
        if (!visionEventColumns.Contains("Count")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN Count INTEGER NULL;");
        if (!visionEventColumns.Contains("AttributesJson")) db.Database.ExecuteSqlRaw("ALTER TABLE VisionEvents ADD COLUMN AttributesJson TEXT NULL;");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_VisionEvents_SopRunId ON VisionEvents (SopRunId);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_InspectionRecords_SourceRecordId ON InspectionRecords (SourceRecordId);");

        // 商用化基线：旧版本数据库通过 IF NOT EXISTS 原子补齐新表；后续迁移只增加版本，不覆盖业务数据。
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version TEXT NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
                AppliedAtUtc TEXT NOT NULL,
                Description TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER NOT NULL CONSTRAINT PK_Users PRIMARY KEY AUTOINCREMENT,
                UserName TEXT NOT NULL, PasswordHash TEXT NOT NULL, IsEnabled INTEGER NOT NULL DEFAULT 1,
                FailedLoginCount INTEGER NOT NULL DEFAULT 0, LockoutUntilUtc TEXT NULL,
                MustChangePassword INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL, LastLoginAtUtc TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_UserName ON Users(UserName COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS Roles (
                Id INTEGER NOT NULL CONSTRAINT PK_Roles PRIMARY KEY AUTOINCREMENT,
                Code TEXT NOT NULL, Name TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Roles_Code ON Roles(Code COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS Permissions (
                Id INTEGER NOT NULL CONSTRAINT PK_Permissions PRIMARY KEY AUTOINCREMENT,
                Code TEXT NOT NULL, Name TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Permissions_Code ON Permissions(Code COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS UserRoles (
                UserId INTEGER NOT NULL, RoleId INTEGER NOT NULL,
                CONSTRAINT PK_UserRoles PRIMARY KEY(UserId, RoleId),
                FOREIGN KEY(UserId) REFERENCES Users(Id), FOREIGN KEY(RoleId) REFERENCES Roles(Id)
            );
            CREATE TABLE IF NOT EXISTS RolePermissions (
                RoleId INTEGER NOT NULL, PermissionId INTEGER NOT NULL,
                CONSTRAINT PK_RolePermissions PRIMARY KEY(RoleId, PermissionId),
                FOREIGN KEY(RoleId) REFERENCES Roles(Id), FOREIGN KEY(PermissionId) REFERENCES Permissions(Id)
            );
            CREATE TABLE IF NOT EXISTS LoginEvents (
                Id INTEGER NOT NULL CONSTRAINT PK_LoginEvents PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NULL, UserName TEXT NOT NULL, Succeeded INTEGER NOT NULL,
                Reason TEXT NOT NULL, ClientAddress TEXT NULL, OccurredAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LoginEvents_OccurredAtUtc ON LoginEvents(OccurredAtUtc);
            CREATE INDEX IF NOT EXISTS IX_LoginEvents_UserName ON LoginEvents(UserName);
            CREATE TABLE IF NOT EXISTS AuditEvents (
                Id INTEGER NOT NULL CONSTRAINT PK_AuditEvents PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL, ActorUserId INTEGER NULL, ActorName TEXT NOT NULL,
                Action TEXT NOT NULL, ObjectType TEXT NOT NULL, ObjectId TEXT NULL,
                Result TEXT NOT NULL, DetailsJson TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL,
                PreviousHash TEXT NULL, Hash TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AuditEvents_EventId ON AuditEvents(EventId);
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_OccurredAtUtc ON AuditEvents(OccurredAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_Object ON AuditEvents(ObjectType, ObjectId);
            CREATE TABLE IF NOT EXISTS BackupRecords (
                Id INTEGER NOT NULL CONSTRAINT PK_BackupRecords PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL, Kind TEXT NOT NULL, Status TEXT NOT NULL,
                Sha256 TEXT NULL, SizeBytes INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL, Error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_BackupRecords_CreatedAtUtc ON BackupRecords(CreatedAtUtc);
            CREATE TABLE IF NOT EXISTS HealthSnapshots (
                Id INTEGER NOT NULL CONSTRAINT PK_HealthSnapshots PRIMARY KEY AUTOINCREMENT,
                State TEXT NOT NULL, CpuUsage REAL NOT NULL, MemoryUsedBytes INTEGER NOT NULL,
                MemoryTotalBytes INTEGER NOT NULL, GpuUsedBytes INTEGER NOT NULL,
                GpuTotalBytes INTEGER NOT NULL, FreeDiskBytes INTEGER NOT NULL,
                CapturedAtUtc TEXT NOT NULL, DetailsJson TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_HealthSnapshots_CapturedAtUtc ON HealthSnapshots(CapturedAtUtc);
            CREATE TABLE IF NOT EXISTS Alerts (
                Id INTEGER NOT NULL CONSTRAINT PK_Alerts PRIMARY KEY AUTOINCREMENT,
                Code TEXT NOT NULL, Severity TEXT NOT NULL, Title TEXT NOT NULL,
                DetailsJson TEXT NOT NULL, Status TEXT NOT NULL, FirstSeenAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL, AcknowledgedAtUtc TEXT NULL,
                RecoveredAtUtc TEXT NULL, AcknowledgedBy TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Alerts_ActiveCode ON Alerts(Code) WHERE Status = 'active';
            DROP INDEX IF EXISTS IX_Alerts_Code_Status;
            CREATE INDEX IF NOT EXISTS IX_Alerts_LastSeenAtUtc ON Alerts(LastSeenAtUtc);
            INSERT OR IGNORE INTO SchemaMigrations(Version, AppliedAtUtc, Description)
                VALUES ('20260906-commercial-foundation', CURRENT_TIMESTAMP, '商用化数据安全底座');
            CREATE TABLE IF NOT EXISTS CommunicationRequests (
                Id INTEGER NOT NULL CONSTRAINT PK_CommunicationRequests PRIMARY KEY AUTOINCREMENT,
                ProjectCode TEXT NOT NULL, ClientId TEXT NOT NULL, RequestId TEXT NOT NULL,
                Command TEXT NOT NULL, RequestHash TEXT NOT NULL, Status TEXT NOT NULL,
                ResponseJson TEXT NULL, ReceivedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL,
                ExpiresAtUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CommunicationRequests_Key
                ON CommunicationRequests(ProjectCode, ClientId, RequestId);
            CREATE INDEX IF NOT EXISTS IX_CommunicationRequests_ExpiresAtUtc
                ON CommunicationRequests(ExpiresAtUtc);
            CREATE TABLE IF NOT EXISTS RecipeVersions (
                Id INTEGER NOT NULL CONSTRAINT PK_RecipeVersions PRIMARY KEY AUTOINCREMENT,
                RecipeCode TEXT NOT NULL, Version INTEGER NOT NULL, State TEXT NOT NULL,
                DefinitionJson TEXT NOT NULL, ContentSha256 TEXT NOT NULL, PublishedBy TEXT NULL,
                CreatedAtUtc TEXT NOT NULL, PublishedAtUtc TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_RecipeVersions_Key ON RecipeVersions(RecipeCode, Version);
            CREATE INDEX IF NOT EXISTS IX_RecipeVersions_State ON RecipeVersions(RecipeCode, State);
            CREATE TABLE IF NOT EXISTS ModelArtifacts (
                Id INTEGER NOT NULL CONSTRAINT PK_ModelArtifacts PRIMARY KEY AUTOINCREMENT,
                ModelCode TEXT NOT NULL, Version INTEGER NOT NULL, FilePath TEXT NOT NULL,
                Sha256 TEXT NOT NULL, Algorithm TEXT NULL, State TEXT NOT NULL,
                PublishedBy TEXT NULL, CreatedAtUtc TEXT NOT NULL, PublishedAtUtc TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ModelArtifacts_Key ON ModelArtifacts(ModelCode, Version);
            CREATE INDEX IF NOT EXISTS IX_ModelArtifacts_Sha256 ON ModelArtifacts(Sha256);
            CREATE TABLE IF NOT EXISTS DeploymentBindings (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentBindings PRIMARY KEY AUTOINCREMENT,
                TargetCode TEXT NOT NULL, RecipeCode TEXT NOT NULL, RecipeVersion INTEGER NOT NULL,
                ModelCode TEXT NOT NULL, ModelVersion INTEGER NOT NULL, State TEXT NOT NULL,
                UpdatedBy TEXT NULL, UpdatedAtUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentBindings_TargetCode ON DeploymentBindings(TargetCode);
            CREATE TABLE IF NOT EXISTS LicenseEvents (
                Id INTEGER NOT NULL CONSTRAINT PK_LicenseEvents PRIMARY KEY AUTOINCREMENT,
                LicenseId TEXT NOT NULL, EventType TEXT NOT NULL, DetailsJson TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LicenseEvents_OccurredAtUtc ON LicenseEvents(OccurredAtUtc);
            CREATE TABLE IF NOT EXISTS DatasetVersions (
                Id INTEGER NOT NULL CONSTRAINT PK_DatasetVersions PRIMARY KEY AUTOINCREMENT,
                DatasetCode TEXT NOT NULL, Version INTEGER NOT NULL, Sha256 TEXT NOT NULL,
                TrainingCount INTEGER NOT NULL, ValidationCount INTEGER NOT NULL, CreatedBy TEXT NULL, CreatedAtUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DatasetVersions_Key ON DatasetVersions(DatasetCode, Version);
            CREATE INDEX IF NOT EXISTS IX_DatasetVersions_Sha256 ON DatasetVersions(Sha256);
            CREATE TABLE IF NOT EXISTS ReportJobs (
                Id INTEGER NOT NULL CONSTRAINT PK_ReportJobs PRIMARY KEY AUTOINCREMENT,
                Status TEXT NOT NULL, Format TEXT NOT NULL, FromUtc TEXT NOT NULL, ToUtc TEXT NOT NULL,
                OutputPath TEXT NULL, CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ReportJobs_CreatedAtUtc ON ReportJobs(CreatedAtUtc);
            """);
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
