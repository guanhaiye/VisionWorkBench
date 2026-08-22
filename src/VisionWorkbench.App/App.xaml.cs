using System.Windows;

namespace VisionWorkbench.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppServices.Instance.Initialize();
        var shell = new Shell();
        MainWindow = shell;
        shell.Show();
        // 软件启动阶段立即开始预热，但不阻塞 WPF 主窗口创建。
        _ = AppServices.Instance.SmartAnnotations.WarmupYoloEAsync()
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"YOLOE 启动预热失败：{task.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 优雅退出：算法 Worker 全部关闭（PLG-011/STB-004）
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
        base.OnExit(e);
    }
}
