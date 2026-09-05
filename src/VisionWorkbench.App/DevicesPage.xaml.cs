using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.App;

/// <summary>设备管理页（文档 §8.7）：Provider 发现与测试预览（CAM-001/002）。</summary>
public partial class DevicesPage : UserControl
{
    private readonly PreviewRenderer _preview = new();
    private ICameraSession? _testSession;

    public DevicesPage()
    {
        InitializeComponent();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        TestStatusText.Text = "扫描中…";
        try
        {
            var devices = await AppServices.Instance.Cameras.DiscoverAllAsync(CancellationToken.None);
            DevicesGrid.ItemsSource = devices;
            TestStatusText.Text = $"发现 {devices.Count} 台（虚拟源不参与发现，在任务配置里直接填路径）";
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"扫描失败: {ex.Message}";
        }
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not CameraDescriptor descriptor)
        {
            ThemedMessageBox.Show("先扫描并选择设备", "提示");
            return;
        }
        if (_testSession is not null)
        {
            return; // 上一个测试还在进行
        }
        TestStatusText.Text = $"打开 {descriptor.DisplayName}…";
        try
        {
            var session = await AppServices.Instance.Cameras.OpenSessionAsync(
                descriptor, new CameraOpenOptions(), CancellationToken.None);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Faulted += (s, args) =>
            {
                Dispatcher.BeginInvoke(() => TestStatusText.Text = $"故障: {args.Fault.Message}");
                opened.TrySetResult();
            };
            await session.OpenAsync(new CameraOpenOptions(), CancellationToken.None);
            session.FrameReceived += (s, args) => Dispatcher.BeginInvoke(() =>
                _preview.Render(TestPreviewImage, args.Frame));
            await session.StartAsync(CancellationToken.None);
            _testSession = session;
            TestStatusText.Text = "预览中（5 秒后自动关闭）…";
            await Task.Delay(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
            _testSession = null;
            TestStatusText.Text = "测试完成";
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"测试失败: {ex.Message}";
            _testSession = null;
        }
    }
}
