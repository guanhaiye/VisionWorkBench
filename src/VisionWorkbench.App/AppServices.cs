using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Cameras.Files;
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
        Path.Combine(FindProjectRoot(), "data");
    /// <summary>标注平台新建数据集的独立存储根目录，默认使用 E 盘。</summary>
    public string DatasetDirectory { get; set; } = @"E:\";
    public string? PluginsRoot { get; set; }
    public string? PythonExecutable { get; set; }
    public string? YoloeModelPath { get; set; }
    public string? Sam1ModelPath { get; set; }
    // 兼容早期预览版设置文件；新配置统一使用 Sam1ModelPath。
    public string? Sam3ModelPath { get; set; }
    public string ExecutionProvider { get; set; } = "cpu";
    public string CurrentRole { get; set; } = "engineer";
    public string OperatorName { get; set; } = "";
    public string? AvatarPath { get; set; }
    public string? PasswordHash { get; set; }
    public List<UserAccount> Accounts { get; set; } = [];
    public string ThemeMode { get; set; } = "light";
    public double UiScale { get; set; } = 1.0;
    public string? ResultWebhookUrl { get; set; }
    public bool EnableHistory { get; set; } = true;
    /// <summary>实时检测页面的窗口布局。</summary>
    public string LiveLayout { get; set; } = "grid";
    /// <summary>实时检测页面上次添加的任务面板。</summary>
    public List<long> LiveTaskIds { get; set; } = [];
    /// <summary>实时检测页面按任务保存的运行时 ROI 覆盖设置。</summary>
    public Dictionary<long, NormalizedRect?> LiveRois { get; set; } = [];

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
    public ResultPublisher ResultPublisher { get; private set; } = null!;
    public TaskRepository Tasks { get; private set; } = null!;
    public ProjectStationRepository Projects { get; private set; } = null!;
    public RecordRepository Records { get; private set; } = null!;
    public BatchRepository Batches { get; private set; } = null!;
    public RecipeService Recipes { get; private set; } = null!;
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
        // 1. 配置：exe 目录 appsettings.json + 数据目录 settings.json（用户可改项）
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

        Directory.CreateDirectory(Settings.DataDirectory);
        SettingsFile = Path.Combine(Settings.DataDirectory, "settings.json");
        if (File.Exists(SettingsFile))
        {
            try
            {
                var user = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (user is not null)
                {
                    user.DataDirectory = Settings.DataDirectory; // 数据目录自身不可自指覆盖
                    Settings = user;
                }
            }
            catch (JsonException)
            {
                // 用户配置损坏 → 用默认
            }
        }

        EnsureAccounts();

        // 2. 日志（DIA-003：application / algorithm-manager / database / camera 分文件）
        var logsDir = Path.Combine(Settings.DataDirectory, "logs");
        LoggerFactory = LogSetup.CreateFactory(logsDir);

        // 3. 数据库（APP-003：数据目录支持中文/空格；工厂模式保证多线程安全）
        var dbPath = Path.Combine(Settings.DataDirectory, "visionworkbench.db");
        Database = VisionDbContextFactory.Create(dbPath);
        DatabaseBackup = new DatabaseBackupService(Database);
        ResultPublisher = new ResultPublisher(
            Path.Combine(Settings.DataDirectory, "results", "results.jsonl"),
            Settings.ResultWebhookUrl);
        Datasets = new DatasetCatalogService(Settings.DataDirectory);
        Tasks = new TaskRepository(Database);
        Projects = new ProjectStationRepository(Database);
        Records = new RecordRepository(Database);
        Batches = new BatchRepository(Database);
        Recipes = new RecipeService(Tasks);
        BatchService = new BatchService(Batches);
        TcpCommunication = new ProjectCommunicationManager(
            Projects, Tasks, LoggerFactory.CreateLogger<ProjectCommunicationManager>(),
            Path.Combine(logsDir, "tcp"));

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
            () => Settings.EnableHistory);
        TcpCommunication.TaskExecutor = new TcpTaskExecutionService(this).ExecuteAsync;

        // 7. DI 容器（页面按需取服务）
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(this);
        Services = services.BuildServiceProvider();
    }

    public void SaveUserSettings()
    {
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFile, json);
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
        // workers/.venv/Scripts/python.exe（开发态默认，宿主可配置覆盖）
        var candidates = new[]
        {
            Path.Combine(pluginsRoot, ".venv", "Scripts", "python.exe"),
            Path.Combine(Directory.GetParent(pluginsRoot)?.FullName ?? "", ".venv", "Scripts", "python.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
