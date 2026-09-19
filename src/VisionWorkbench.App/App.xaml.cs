using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
using VisionWorkbench.Application.Communication;
using System.Windows.Interop;

namespace VisionWorkbench.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\VisionWorkbench.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private Shell? _shell;
    private bool _isShuttingDown;
    private bool _environmentCheckMode;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var environmentCheck = e.Args.Any(argument =>
            string.Equals(argument, "--environment-check", StringComparison.OrdinalIgnoreCase));
        WaitForRestartProcess(e.Args);
        _environmentCheckMode = environmentCheck;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName,
            out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            if (ActivateExistingInstance())
            {
                Shutdown();
                return;
            }

            // 旧实例可能只剩后台进程而没有任何窗口。清理后重新取得互斥体，
            // 避免“软件已启动但用户看不到窗口”阻止后续启动。
            try
            {
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
            }
            if (!_ownsSingleInstanceMutex)
            {
                Shutdown();
                return;
            }
        }
        // 启动页先于主窗口显示时，WPF 可能自动把启动页认作 MainWindow；
        // 若此时使用 OnMainWindowClose，关闭启动页会连带退出整个应用。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SplashWindow? splash = null;
        if (!environmentCheck)
        {
            splash = new SplashWindow();
            splash.Show();
            Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        }
        try
        {
            // 初始化包含数据库建表、完整性检查和权限种子写入，不能阻塞 WPF UI 线程。
            await Task.Run(AppServices.Instance.Initialize);
            if (environmentCheck)
            {
                var exitCode = await EnvironmentCheckService.RunAsync();
                Environment.Exit(exitCode);
                Shutdown(exitCode);
                return;
            }
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
            _shell.Visibility = Visibility.Visible;
            _shell.WindowState = WindowState.Normal;
            _shell.ShowInTaskbar = true;
            _shell.Activate();
            var shellHandle = new WindowInteropHelper(_shell).Handle;
            if (shellHandle != IntPtr.Zero)
            {
                ShowWindowAsync(shellHandle, 9); // SW_RESTORE
                SetForegroundWindow(shellHandle);
            }
            splash?.Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), ex.ToString()); } catch { }
            ThemedMessageBox.Show(ex.ToString(), "VisionWorkbench 界面启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            splash?.Close();
            Shutdown(-1);
            return;
        }
        // 智能标注模型改为首次使用时按需启动，避免 Python/模型环境异常拖住主界面启动和退出。
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
            var profiles = new TcpCommunicationProfileStore(AppServices.Instance.Settings.ConfigDirectory).Load();
            var config = profiles.FirstOrDefault(x => x.Enabled && x.AutoStart);
            if (config is not null) _ = AppServices.Instance.TcpCommunication.StartAsync(config);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TCP 自动启动失败: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_environmentCheckMode)
        {
            if (_ownsSingleInstanceMutex)
            {
                try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            }
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
            return;
        }
        // 优雅退出：算法 Worker 全部关闭（PLG-011/STB-004）
        try
        {
            AppServices.Instance.TcpCommunication.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
        try { AppServices.Instance.AutomaticBackups.Dispose(); } catch (Exception) { }
        try { AppServices.Instance.HealthMonitor.Dispose(); } catch (Exception) { }
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
            AppServices.Instance.SopProductResultReplayer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // SOP 结果重放器关闭失败不影响应用退出
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

    private static bool ActivateExistingInstance()
    {
        var existing = Process.GetProcessesByName("VisionWorkbench")
            .FirstOrDefault(process => process.Id != Environment.ProcessId
                && !process.HasExited);
        if (existing is null) return false;

        // 正常启动中的实例可能需要几秒才创建主窗口。
        for (var attempt = 0; attempt < 32; attempt++)
        {
            try
            {
                existing.Refresh();
                if (existing.HasExited) return false;
                if (existing.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(existing.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(existing.MainWindowHandle);
                    return true;
                }
            }
            catch
            {
                return false;
            }
            Thread.Sleep(250);
        }

        // 等待后仍没有窗口，说明是启动异常留下的后台残留进程。
        try
        {
            if (!existing.HasExited)
            {
                existing.Kill(entireProcessTree: true);
                existing.WaitForExit(3000);
            }
        }
        catch
        {
            // 由后续互斥体获取结果决定是否继续启动。
        }
        return false;
    }

    private static void WaitForRestartProcess(string[] args)
    {
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, "--restart-wait-pid", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var processId)
            || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException)
                {
                    // 旧进程已经退出。
                }
            }
        }
        catch (ArgumentException)
        {
            // 旧进程已经退出。
        }
        catch (InvalidOperationException)
        {
            // 旧进程已经退出或句柄不可用。
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
