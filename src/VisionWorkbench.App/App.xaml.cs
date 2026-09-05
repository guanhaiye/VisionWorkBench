using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
using VisionWorkbench.Application.Communication;

namespace VisionWorkbench.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\VisionWorkbench.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private Shell? _shell;
    private bool _isShuttingDown;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName,
            out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }
        // 启动页先于主窗口显示时，WPF 可能自动把启动页认作 MainWindow；
        // 若此时使用 OnMainWindowClose，关闭启动页会连带退出整个应用。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new SplashWindow();
        splash.Show();
        Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        try
        {
            AppServices.Instance.Initialize();
            ThemeManager.Apply(AppServices.Instance.Settings.ThemeMode);
            ThemeManager.ApplyUiScale(AppServices.Instance.Settings.UiScale);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), ex.ToString());
            }
            catch { }
            ThemedMessageBox.Show(ex.ToString(), "VisionWorkbench 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        // TCP 通讯改为进入通讯页面后启动，避免通讯初始化影响主窗口启动。
        try
        {
            _shell = new Shell();
            MainWindow = _shell;
            _shell.ShowInTaskbar = true;
            _shell.Closed += (_, _) =>
            {
                if (!_isShuttingDown)
                {
                    _isShuttingDown = true;
                    Shutdown();
                }
            };
            _shell.Show();
            _shell.Activate();
            splash.Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), ex.ToString()); } catch { }
            ThemedMessageBox.Show(ex.ToString(), "VisionWorkbench 界面启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            splash.Close();
            Shutdown(-1);
            return;
        }
        // 软件启动阶段立即开始预热，但不阻塞 WPF 主窗口创建。
        _ = AppServices.Instance.SmartAnnotations.WarmupYoloEAsync()
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"YOLOE 启动预热失败：{task.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
        _ = AppServices.Instance.SmartAnnotations.WarmupSam1Async()
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"SAM1 启动预热失败：{task.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "unhandled-error.log"), e.Exception.ToString()); } catch { }
        e.Handled = true;
        ThemedMessageBox.Show(e.Exception.ToString(), "VisionWorkbench 运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "unobserved-task-error.log"), e.Exception.ToString()); } catch { }
        e.SetObserved();
    }

    private static void StartConfiguredTcpCommunication()
    {
        try
        {
            var path = Path.Combine(AppServices.Instance.Settings.DataDirectory, "tcp-communication.json");
            if (!File.Exists(path)) return;
            var configs = JsonSerializer.Deserialize<Dictionary<string, ProjectCommunicationConfig>>(File.ReadAllText(path));
            var config = configs?.Values.FirstOrDefault(x => x.Enabled && x.AutoStart);
            if (config is not null) _ = AppServices.Instance.TcpCommunication.StartAsync(config);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TCP 自动启动失败: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 优雅退出：算法 Worker 全部关闭（PLG-011/STB-004）
        try
        {
            AppServices.Instance.TcpCommunication.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
        try
        {
            AppServices.Instance.StationRuns.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退出路径尽力释放各工位资源
        }
        try
        {
            AppServices.Instance.AlgorithmManager.ShutdownAllAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退出路径尽力而为
        }
        try
        {
            AppServices.Instance.SmartAnnotations.Dispose();
        }
        catch (Exception)
        {
            // 智能标注 Worker 关闭失败不影响应用退出
        }
        try
        {
            AppServices.Instance.Yolo11Training.Dispose();
        }
        catch (Exception)
        {
            // 训练 Worker 关闭失败不影响应用退出
        }
        try
        {
            AppServices.Instance.Atu5Training.Dispose();
        }
        catch (Exception)
        {
            // ATU5 Worker 关闭失败不影响应用退出
        }
        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
        var existing = Process.GetProcessesByName("VisionWorkbench")
            .FirstOrDefault(process => process.Id != Environment.ProcessId
                && process.MainWindowHandle != IntPtr.Zero);
        if (existing is null) return;
        ShowWindowAsync(existing.MainWindowHandle, 9); // SW_RESTORE
        SetForegroundWindow(existing.MainWindowHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
