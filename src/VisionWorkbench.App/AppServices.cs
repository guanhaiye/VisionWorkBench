using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Cameras.Files;
using VisionWorkbench.Cameras.Hikvision;
using VisionWorkbench.Cameras.Usb;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Infrastructure.Logging;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>应用配置（appsettings.json，可被用户目录覆盖项合并）。</summary>
public sealed class AppSettings
{
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VisionWorkbench");
    /// <summary>SOP 录制视频、原始帧和步骤片段的独立存储目录。</summary>
    public string SopRecordingDirectory { get; set; } = "";
    /// <summary>软件可持久化参数的统一目录。</summary>
    [JsonIgnore]
    public string ConfigDirectory => Path.Combine(DataDirectory, "Config");
    /// <summary>标注平台新建数据集的独立存储根目录，默认使用 E 盘。</summary>
    public string DatasetDirectory { get; set; } = @"E:\";
    public string? PluginsRoot { get; set; }
    public string? PythonExecutable { get; set; }
    public string? YoloeModelPath { get; set; }
    public string? Sam1ModelPath { get; set; }
    // 兼容早期预览版设置文件；新配置统一使用 Sam1ModelPath。
    public string? Sam3ModelPath { get; set; }
    /// <summary>许可证验证公钥，仅允许放置公钥，不允许放置签发私钥。</summary>
    public string? LicensePublicKey { get; set; }
    public string ExecutionProvider { get; set; } = "cpu";
    public string CurrentRole { get; set; } = "engineer";
    public string OperatorName { get; set; } = "";
    public string? AvatarPath { get; set; }
    public string? PasswordHash { get; set; }
    public bool RememberLoginPassword { get; set; }
    public bool AutoLogin { get; set; }
    public string? RememberedLoginUserName { get; set; }
    /// <summary>使用当前 Windows 用户保护的登录密码，不保存明文密码。</summary>
    public string? RememberedLoginPassword { get; set; }
    public List<UserAccount> Accounts { get; set; } = [];
    public string ThemeMode { get; set; } = "light";
    public double UiScale { get; set; } = 1.0;
    public string? ResultWebhookUrl { get; set; }
    public bool EnableHistory { get; set; } = true;
    /// <summary>是否启用参数自动备份。</summary>
    public bool AutomaticBackupEnabled { get; set; } = true;
    /// <summary>参数自动备份间隔，单位为分钟。</summary>
    public int AutomaticBackupIntervalMinutes { get; set; } = 1440;
    /// <summary>自动备份最多保留的版本数量。</summary>
    public int AutomaticBackupRetentionCount { get; set; } = 30;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogPersistenceLevel LogPersistenceLevel { get; set; } = VisionWorkbench.Infrastructure.Logging.LogPersistenceLevel.All;
    /// <summary>实时检测页面的窗口布局。</summary>
    public string LiveLayout { get; set; } = "grid";
    /// <summary>实时检测页面上次添加的任务面板。</summary>
    public List<long> LiveTaskIds { get; set; } = [];
    /// <summary>实时检测页面按任务保存的运行时 ROI 覆盖设置。</summary>
    public Dictionary<long, NormalizedRect?> LiveRois { get; set; } = [];
    /// <summary>按 Provider 与设备 ID 保存的工业相机默认参数。</summary>
    public Dictionary<string, CameraParameterSet> CameraParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FindProjectRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var index = 0; index < 8 && directory is not null; index++, directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "workers")))
                {
                    return directory.FullName;
                }
            }
        }
        return AppContext.BaseDirectory;
    }
}

/// <summary>组合根：进程级服务装配（文档 §5）。UI 线程与后台服务共享同一实例。</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public AppSettings Settings { get; private set; } = new();
    public IServiceProvider Services { get; private set; } = null!;
    public ILoggerFactory LoggerFactory { get; private set; } = null!;
    public AlgorithmManager AlgorithmManager { get; private set; } = null!;
    public CameraRegistry Cameras { get; private set; } = null!;
    public TempImageStore TempImages { get; private set; } = null!;
    public VisionDbContextFactory Database { get; private set; } = null!;
    public DatabaseBackupService DatabaseBackup { get; private set; } = null!;
    public DatabaseMigrationService DatabaseMigrations { get; private set; } = null!;
    public BackupPackageService BackupPackages { get; private set; } = null!;
    public AutomaticBackupService AutomaticBackups { get; private set; } = null!;
    public AssetGovernanceService AssetGovernance { get; private set; } = null!;
    public DataRetentionService DataRetention { get; private set; } = null!;
    public ReleaseManifestService Releases { get; private set; } = null!;
    public OfflineUpdateService OfflineUpdates { get; private set; } = null!;
    public DeviceAdapterRegistry DeviceAdapters { get; private set; } = null!;
    public AuditService Audit { get; private set; } = null!;
    public AccessControlService AccessControl { get; private set; } = null!;
    public IdentityService Identity { get; private set; } = null!;
    public SessionService Session { get; private set; } = null!;
    public DatabaseIntegrityService DatabaseIntegrity { get; private set; } = null!;
    public HealthService Health { get; private set; } = null!;
    public HealthMetricsCollector HealthMetrics { get; private set; } = null!;
    public HealthMonitorService HealthMonitor { get; private set; } = null!;
    public HealthRecoveryService Recovery { get; private set; } = null!;
    public ReportingService Reporting { get; private set; } = null!;
    public FeedbackService Feedback { get; private set; } = null!;
    public DatasetVersionService DatasetVersions { get; private set; } = null!;
    public LicenseService License { get; private set; } = null!;
    public ResultPublisher ResultPublisher { get; private set; } = null!;
    public TaskRepository Tasks { get; private set; } = null!;
    public ProjectStationRepository Projects { get; private set; } = null!;
    public RecordRepository Records { get; private set; } = null!;
    public SopRunRepository SopRuns { get; private set; } = null!;
    public SopProductResultReplayer SopProductResultReplayer { get; private set; } = null!;
    public BatchRepository Batches { get; private set; } = null!;
    public RecipeService Recipes { get; private set; } = null!;
    public SopDefinitionCatalogService SopDefinitions { get; private set; } = null!;
    public BatchService BatchService { get; private set; } = null!;
    public ReInferenceService ReInference { get; private set; } = null!;
    public StationRunCoordinator StationRuns { get; private set; } = null!;
    public DatasetCatalogService Datasets { get; private set; } = null!;
    public SmartAnnotationService SmartAnnotations { get; private set; } = null!;
    public Yolo11TrainingService Yolo11Training { get; private set; } = null!;
    public Atu5TrainingService Atu5Training { get; private set; } = null!;
    public Atu5ModelTestService Atu5ModelTest { get; private set; } = null!;
    public YoloModelTestService YoloModelTest { get; private set; } = null!;
    public BehaviorTrainingService BehaviorTraining { get; private set; } = null!;
    public ProjectCommunicationManager TcpCommunication { get; private set; } = null!;
    public string SettingsFile { get; private set; } = "";

    private AppServices() { }

    /// <summary>初始化：配置 → 日志 → 数据库 → 相机/算法/应用服务。</summary>
    public void Initialize()
    {
        // 1. 配置：exe 目录 appsettings.json（只读默认值）+ Config/settings.json（用户可改项）
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
        Settings = new AppSettings();
        config.GetSection("App").Bind(Settings);
        // 配置里的空字符串不能覆盖代码默认值（Binder 会原样写入 ""）
        if (string.IsNullOrWhiteSpace(Settings.DataDirectory))
        {
            Settings.DataDirectory = new AppSettings().DataDirectory;
        }
        if (string.IsNullOrWhiteSpace(Settings.DatasetDirectory))
        {
            Settings.DatasetDirectory = @"E:\";
        }
        Settings.PluginsRoot = string.IsNullOrWhiteSpace(Settings.PluginsRoot) ? null : Settings.PluginsRoot;
        Settings.PythonExecutable = string.IsNullOrWhiteSpace(Settings.PythonExecutable) ? null : Settings.PythonExecutable;
        Settings.OperatorName = string.IsNullOrWhiteSpace(Settings.OperatorName)
            ? Environment.UserName
            : Settings.OperatorName;
        var configuredLicensePublicKey = Settings.LicensePublicKey;

        Directory.CreateDirectory(Settings.DataDirectory);
        Directory.CreateDirectory(Settings.ConfigDirectory);
        MigrateLegacyConfigFile("settings.json");
        SettingsFile = Path.Combine(Settings.ConfigDirectory, "settings.json");
        if (File.Exists(SettingsFile))
        {
            try
            {
                var user = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (user is not null)
                {
                    user.DataDirectory = Settings.DataDirectory; // 数据目录自身不可自指覆盖
                    // 用户配置中的空公钥不能覆盖程序目录中配置的许可证公钥。
                    if (string.IsNullOrWhiteSpace(user.LicensePublicKey))
                    {
                        user.LicensePublicKey = configuredLicensePublicKey;
                    }
                    Settings = user;
                }
            }
            catch (JsonException)
            {
                // 用户配置损坏 → 用默认
            }
        }

        if (string.IsNullOrWhiteSpace(Settings.SopRecordingDirectory))
        {
            Settings.SopRecordingDirectory = Path.Combine(Settings.DataDirectory, "sop-recordings");
        }
        else
        {
            try
            {
                Settings.SopRecordingDirectory = Path.GetFullPath(Settings.SopRecordingDirectory);
            }
            catch
            {
                Settings.SopRecordingDirectory = Path.Combine(Settings.DataDirectory, "sop-recordings");
            }
        }
        Directory.CreateDirectory(Settings.SopRecordingDirectory);

        EnsureAccounts();

        // 2. 日志（DIA-003：application / algorithm-manager / database / camera 分文件）
        foreach (var fileName in new[] { "visionworkbench.db", "visionworkbench.db-wal", "visionworkbench.db-shm", "datasets.json", "tcp-communication.json", "license.json", "license-clock.dat", "license-clock.v2", "license-clock.json" })
        {
            MigrateLegacyConfigFile(fileName);
        }

        var logsDir = Path.Combine(Settings.DataDirectory, "logs");
        LogSetup.SetPersistenceLevel(Settings.LogPersistenceLevel);
        LoggerFactory = LogSetup.CreateFactory(logsDir);

        // 3. 数据库（APP-003：数据目录支持中文/空格；工厂模式保证多线程安全）
        var dbPath = Path.Combine(Settings.ConfigDirectory, "visionworkbench.db");
        Database = VisionDbContextFactory.Create(dbPath);
        DatabaseBackup = new DatabaseBackupService(Database);
        DatabaseMigrations = new DatabaseMigrationService(Database);
        BackupPackages = new BackupPackageService(Database, SettingsFile, Settings.DataDirectory);
        AutomaticBackups = new AutomaticBackupService(
            BackupPackages,
            SettingsFile,
            Settings.DataDirectory,
            Settings.AutomaticBackupEnabled,
            Settings.AutomaticBackupIntervalMinutes,
            Settings.AutomaticBackupRetentionCount);
        Audit = new AuditService(Database);
        BackupPackages.AuditSink = Audit;
        DataRetention = new DataRetentionService(Database);
        Releases = new ReleaseManifestService();
        OfflineUpdates = new OfflineUpdateService(Releases);
        DeviceAdapters = new DeviceAdapterRegistry();
        AccessControl = new AccessControlService(Database);
        AssetGovernance = new AssetGovernanceService(Database, Audit, AccessControl);
        Identity = new IdentityService(Database, Audit);
        Session = new SessionService();
        DatabaseIntegrity = new DatabaseIntegrityService(Database);
        Health = new HealthService(Database);
        HealthMetrics = new HealthMetricsCollector(Health, Settings.DataDirectory);
        HealthMonitor = new HealthMonitorService(HealthMetrics);
        Recovery = new HealthRecoveryService(Health);
        Reporting = new ReportingService(Database);
        Feedback = new FeedbackService(Database);
        DatasetVersions = new DatasetVersionService(Database);
        License = new LicenseService(Settings.ConfigDirectory, Settings.LicensePublicKey, Database);
        var schemaStatus = DatabaseMigrations.CheckAsync().GetAwaiter().GetResult();
        if (!schemaStatus.IntegrityOk) throw new InvalidOperationException("DB-001 数据库完整性检查失败");
        AccessControl.EnsureSeededAsync().GetAwaiter().GetResult();
        MigrateAccountsToDatabase();
        ResultPublisher = new ResultPublisher(
            Path.Combine(Settings.DataDirectory, "results", "results.jsonl"),
            Settings.ResultWebhookUrl);
        Datasets = new DatasetCatalogService(Settings.ConfigDirectory);
        Tasks = new TaskRepository(Database);
        Projects = new ProjectStationRepository(Database);
        Records = new RecordRepository(Database);
        SopRuns = new SopRunRepository(Database);
        SopProductResultReplayer = new SopProductResultReplayer(
            SopRuns,
            ResultPublisher,
            LoggerFactory.CreateLogger<SopProductResultReplayer>());
        SopProductResultReplayer.Start();
        Batches = new BatchRepository(Database);
        Recipes = new RecipeService(Tasks);
        SopDefinitions = new SopDefinitionCatalogService(Settings.ConfigDirectory);
        BatchService = new BatchService(Batches);
        TcpCommunication = new ProjectCommunicationManager(
            Projects, Tasks, LoggerFactory.CreateLogger<ProjectCommunicationManager>(),
            Path.Combine(logsDir, "tcp"), new CommunicationRequestStore(Database));

        // 4. 临时图片 + 清理（FRM-005）
        TempImages = new TempImageStore(Path.Combine(Settings.DataDirectory, "temp-images")).Initialize();
        var purged = TempImages.PurgeOlderThan(TimeSpan.FromHours(2));
        if (purged > 0)
        {
            LoggerFactory.CreateLogger<AppServices>()
                .LogInformation("启动清理临时图片 {Count} 张（FRM-005）", purged);
        }

        // 5. 相机 Provider 注册
        var loggerFactory = LoggerFactory;
        Cameras = new CameraRegistry(
        [
            new ImageFolderProvider(loggerFactory.CreateLogger<ImageFolderProvider>()),
            new VideoFileProvider(loggerFactory.CreateLogger<VideoFileProvider>()),
            new NetworkCameraProvider(loggerFactory.CreateLogger<NetworkCameraProvider>()),
            new HikvisionCameraProvider(loggerFactory.CreateLogger<HikvisionCameraProvider>()),
            new UsbCameraProvider(loggerFactory.CreateLogger<UsbCameraProvider>()),
        ],
            loggerFactory.CreateLogger<CameraRegistry>());

        // 6. 算法管理器：插件根目录解析（开发态向上找 workers/，打包态 exe 旁 plugins/）
        var pluginsRoot = Settings.PluginsRoot ?? FindPluginsRoot();
        SmartAnnotations = new SmartAnnotationService(
            pluginsRoot,
            Settings.PythonExecutable,
            Settings.YoloeModelPath,
            Settings.Sam1ModelPath ?? Settings.Sam3ModelPath);
        Yolo11Training = new Yolo11TrainingService(pluginsRoot, Settings.PythonExecutable);
        Atu5Training = new Atu5TrainingService(pluginsRoot, Settings.PythonExecutable);
        Atu5ModelTest = new Atu5ModelTestService(pluginsRoot, Settings.PythonExecutable);
        YoloModelTest = new YoloModelTestService(pluginsRoot, Settings.PythonExecutable);
        BehaviorTraining = new BehaviorTrainingService(pluginsRoot, Settings.PythonExecutable);
        AlgorithmManager = new AlgorithmManager(new AlgorithmManagerOptions
        {
            PluginsRoot = pluginsRoot,
            PythonExecutable = Settings.PythonExecutable ?? FindVenvPython(pluginsRoot),
            LogsDirectory = logsDir,
            ExecutionProvider = Settings.ExecutionProvider,
        }, loggerFactory.CreateLogger<AlgorithmManager>());
        ReInference = new ReInferenceService(Records, Recipes, AlgorithmManager);
        StationRuns = new StationRunCoordinator(
            Records, Batches, TempImages, ResultPublisher,
            loggerFactory.CreateLogger<StationRunCoordinator>(), loggerFactory,
            () => Settings.EnableHistory,
            () => Settings.EnableHistory,
            SopRuns,
            pendingReplayTrigger: SopProductResultReplayer);
        TcpCommunication.TaskExecutor = new TcpTaskExecutionService(this).ExecuteAsync;
        BackupPackages.RestoreGuard = () => StationRuns.RunningStationIds.Count == 0
            && !Yolo11Training.IsRunning
            && !Atu5Training.IsRunning
            && !BehaviorTraining.IsRunning;
        BackupPackages.PreRestoreBackup = ct => BackupPackages.CreateAsync(
            Path.Combine(Settings.DataDirectory, "backups", $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.vwbackup"),
            SettingsFile, Settings.DataDirectory, "pre-restore", ct);

        // 7. DI 容器（页面按需取服务）
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(this);
        Services = services.BuildServiceProvider();
    }

    public void SaveUserSettings()
    {
        LogSetup.SetPersistenceLevel(Settings.LogPersistenceLevel);
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = SettingsFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // 先完整写入临时文件，再一次性替换，避免断电或备份并发读取到半截 JSON。
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>读取设备管理中保存的相机默认参数。</summary>
    public CameraParameterSet? GetCameraParameters(CameraDescriptor descriptor)
    {
        var key = BuildCameraSettingsKey(descriptor.ProviderId, descriptor.DeviceId);
        return Settings.CameraParameters is not null && Settings.CameraParameters.TryGetValue(key, out var parameters)
            ? parameters
            : null;
    }

    /// <summary>保存设备管理中设置的相机默认参数。</summary>
    public void SaveCameraParameters(CameraDescriptor descriptor, CameraParameterSet parameters)
    {
        Settings.CameraParameters ??= new Dictionary<string, CameraParameterSet>(StringComparer.OrdinalIgnoreCase);
        Settings.CameraParameters[BuildCameraSettingsKey(descriptor.ProviderId, descriptor.DeviceId)] = parameters;
        SaveUserSettings();
    }

    /// <summary>将设备管理保存的默认参数与调用方显式参数合并；调用方参数优先。</summary>
    public CameraOpenOptions ApplyCameraDefaults(CameraDescriptor descriptor, CameraOpenOptions options)
    {
        var defaults = GetCameraParameters(descriptor);
        if (defaults is null || !string.Equals(descriptor.ProviderId, "hikvision", StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        var requested = options.Parameters;
        var merged = new CameraParameterSet
        {
            Width = requested?.Width ?? defaults.Width,
            Height = requested?.Height ?? defaults.Height,
            FrameRate = requested?.FrameRate ?? defaults.FrameRate,
            ExposureTimeUs = requested?.ExposureTimeUs ?? defaults.ExposureTimeUs,
            GainDb = requested?.GainDb ?? defaults.GainDb,
            TriggerMode = requested?.TriggerMode ?? defaults.TriggerMode,
            TriggerSource = requested?.TriggerSource ?? defaults.TriggerSource,
            PixelFormat = requested?.PixelFormat ?? defaults.PixelFormat,
            AutoExposure = requested?.AutoExposure ?? defaults.AutoExposure,
            AutoGain = requested?.AutoGain ?? defaults.AutoGain,
        };
        return options with { Parameters = merged };
    }

    private static string BuildCameraSettingsKey(string providerId, string deviceId) =>
        $"{providerId.Trim().ToLowerInvariant()}::{deviceId.Trim()}";

    private void MigrateLegacyConfigFile(string fileName)
    {
        var legacyPath = Path.Combine(Settings.DataDirectory, fileName);
        var configPath = Path.Combine(Settings.ConfigDirectory, fileName);
        if (File.Exists(configPath) || !File.Exists(legacyPath)) return;
        try
        {
            File.Copy(legacyPath, configPath, overwrite: false);
        }
        catch (IOException)
        {
            // 保留旧文件，避免迁移失败时丢失配置。
        }
    }

    /// <summary>把旧版 settings.json 账号一次性迁入 RBAC 表；保留 JSON 仅为兼容旧版本回滚。</summary>
    private void MigrateAccountsToDatabase()
    {
        using var db = Database.CreateDbContext();
        foreach (var account in Settings.Accounts.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
        {
            var user = db.Users.FirstOrDefault(item => item.UserName == account.UserName);
            if (user is null)
            {
                user = new UserEntity
                {
                    UserName = account.UserName.Trim(),
                    PasswordHash = account.PasswordHash,
                    IsEnabled = account.IsEnabled,
                    CreatedAtUtc = account.CreatedAt.ToUniversalTime(),
                    UpdatedAtUtc = DateTime.UtcNow,
                };
                db.Users.Add(user);
                db.SaveChanges();
            }
            else
            {
                user.PasswordHash = account.PasswordHash;
                user.IsEnabled = account.IsEnabled;
                user.UpdatedAtUtc = DateTime.UtcNow;
                db.SaveChanges();
            }

            var roleCode = account.IsSuperAdmin ? "admin" : account.Role.Trim().ToLowerInvariant();
            var role = db.Roles.FirstOrDefault(item => item.Code == roleCode) ?? db.Roles.First(item => item.Code == "operator");
            if (!db.UserRoles.Any(item => item.UserId == user.Id && item.RoleId == role.Id))
            {
                db.UserRoles.Add(new UserRoleEntity { UserId = user.Id, RoleId = role.Id });
                db.SaveChanges();
            }
        }
    }

    private void EnsureAccounts()
    {
        Settings.Accounts ??= [];
        var changed = false;

        // 将旧版本的单用户配置迁移为普通账户，避免升级后原有用户无法登录。
        if (!string.IsNullOrWhiteSpace(Settings.OperatorName)
            && !string.Equals(Settings.OperatorName, "未登录", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(Settings.PasswordHash)
            && Settings.Accounts.All(account =>
                !string.Equals(account.UserName, Settings.OperatorName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Settings.Accounts.Add(new UserAccount
            {
                UserName = Settings.OperatorName.Trim(),
                Role = Settings.CurrentRole,
                AvatarPath = Settings.AvatarPath,
                PasswordHash = Settings.PasswordHash,
                IsEnabled = true,
            });
            changed = true;
        }

        foreach (var account in Settings.Accounts)
        {
            account.UserName = AccountRules.NormalizeUserName(account.UserName);
            if (account.IsSuperAdmin) account.Role = "superadmin";
        }

        if (changed) SaveUserSettings();
    }

    /// <summary>开发态：从 cwd 向上找 workers 目录；找不到用 exe 旁 plugins。</summary>
    private static string FindPluginsRoot()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(bundled))
        {
            return bundled;
        }

        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "workers");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "plugins");
    }

    private static string? FindVenvPython(string pluginsRoot)
    {
        // 发布态优先使用安装目录内置的可迁移 Python，不能依赖客户机器的 PATH 或开发目录。
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "python.exe"),
            Path.Combine(pluginsRoot, ".venv", "Scripts", "python.exe"),
            Path.Combine(Directory.GetParent(pluginsRoot)?.FullName ?? "", ".venv", "Scripts", "python.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
