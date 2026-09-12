using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VisionWorkbench.App;

public partial class SystemMonitorPage : UserControl
{
    private readonly SystemMonitorService _monitor = new();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    public SystemMonitorPage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Timer_Tick;
        Loaded += SystemMonitorPage_Loaded;
        Unloaded += SystemMonitorPage_Unloaded;
    }

    private async void SystemMonitorPage_Loaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        await RefreshAsync();
    }

    private void SystemMonitorPage_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private async void Timer_Tick(object? sender, EventArgs e) => await RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(_monitor.Read);
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            GpuDetailText.Text = $"读取监控数据失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySnapshot(SystemMonitorSnapshot snapshot)
    {
        CpuValueText.Text = $"{snapshot.CpuUsage:0.0}%";
        CpuProgress.Value = snapshot.CpuUsage;
        CpuDetailText.Text = $"当前系统 CPU 使用率 · {DateTime.Now:HH:mm:ss} 更新";

        var memoryPercent = Percent(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes);
        MemoryValueText.Text = $"{memoryPercent:0.0}%";
        MemoryProgress.Value = memoryPercent;
        MemoryDetailText.Text = $"已使用 {ByteSizeFormatter.Format(snapshot.MemoryUsedBytes)} / 共 {ByteSizeFormatter.Format(snapshot.MemoryTotalBytes)}";

        var gpuPercent = Percent(snapshot.GpuUsedBytes, snapshot.GpuTotalBytes);
        GpuValueText.Text = snapshot.GpuTotalBytes > 0 ? $"{gpuPercent:0.0}%" : "--";
        GpuProgress.Value = gpuPercent;
        var gpuUsage = snapshot.GpuUsage.HasValue ? $" · GPU 使用率 {snapshot.GpuUsage:0.0}%" : "";
        GpuDetailText.Text = snapshot.GpuTotalBytes > 0
            ? $"{snapshot.GpuName} · {ByteSizeFormatter.Format(snapshot.GpuUsedBytes)} / {ByteSizeFormatter.Format(snapshot.GpuTotalBytes)}{gpuUsage}"
            : snapshot.GpuName;

        var diskUsed = snapshot.Disks.Aggregate(0UL, (total, disk) => total + disk.UsedBytes);
        var diskTotal = snapshot.Disks.Aggregate(0UL, (total, disk) => total + disk.TotalBytes);
        var diskPercent = Percent(diskUsed, diskTotal);
        DiskValueText.Text = diskTotal > 0 ? $"{diskPercent:0.0}%" : "--";
        DiskProgress.Value = diskPercent;
        DiskItemsControl.ItemsSource = snapshot.Disks;
    }

    private static double Percent(ulong used, ulong total) =>
        total == 0 ? 0 : Math.Clamp(used * 100d / total, 0, 100);

}
