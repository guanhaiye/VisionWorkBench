using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.App;

/// <summary>设备管理页（文档 §8.7）：Provider 发现与测试预览（CAM-001/002）。</summary>
public partial class DevicesPage : UserControl
{
    private readonly PreviewRenderer _preview = new();
    private ICameraSession? _testSession;
    private CancellationTokenSource? _testCancellation;
    private ICameraSession? _cameraSession;
    private bool _testInProgress;
    private bool _cameraOperationInProgress;
    private bool _loaded;

    public DevicesPage()
    {
        InitializeComponent();
        Loaded += DevicesPage_Loaded;
        Unloaded += DevicesPage_Unloaded;
    }

    private async void DevicesPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ScanAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void DevicesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _testCancellation?.Cancel();
        await StopCameraAsync();
    }

    private async Task ScanAsync()
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
        if (DevicesGrid.SelectedItem is not CameraDescriptor descriptor ||
            !string.Equals(descriptor.ProviderId, "hikvision", StringComparison.OrdinalIgnoreCase))
        {
            HikvisionSettingsGroup.Visibility = Visibility.Collapsed;
            return;
        }

        HikvisionSettingsGroup.Visibility = Visibility.Visible;
        SelectedDeviceText.Text = $"已选择：{descriptor.DisplayName}（设置按设备保存，后续任务打开该相机时自动使用）";
        var saved = AppServices.Instance.GetCameraParameters(descriptor);
        ExposureText.Text = saved?.ExposureTimeUs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        ExposureStatusText.Text = saved is null ? "尚未保存设备默认曝光" : "已加载保存的默认曝光";
    }

    private async void ApplyExposure_Click(object sender, RoutedEventArgs e)
    {
        await ConfigureExposureAsync(save: false);
    }

    private async void SaveExposure_Click(object sender, RoutedEventArgs e)
    {
        await ConfigureExposureAsync(save: true);
    }

    private async Task ConfigureExposureAsync(bool save)
    {
        if (DevicesGrid.SelectedItem is not CameraDescriptor descriptor ||
            !string.Equals(descriptor.ProviderId, "hikvision", StringComparison.OrdinalIgnoreCase))
        {
            ThemedMessageBox.Show("请先扫描并选择海康工业相机", "设置曝光");
            return;
        }

        if (!double.TryParse(ExposureText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var exposure) ||
            exposure <= 0)
        {
            ThemedMessageBox.Show("请输入大于 0 的曝光时间，单位为 μs", "设置曝光");
            return;
        }

        var parameters = new CameraParameterSet
        {
            ExposureTimeUs = exposure,
            AutoExposure = false,
        };
        var options = new CameraOpenOptions { Parameters = parameters };
        ICameraSession? session = null;
        try
        {
            ExposureStatusText.Text = "正在连接相机并设置…";
            session = await AppServices.Instance.Cameras.OpenSessionAsync(
                descriptor, options, CancellationToken.None);
            await session.OpenAsync(options, CancellationToken.None);
            if (session.State == CameraSessionState.Faulted)
            {
                throw new InvalidOperationException("相机打开失败，请检查 MVS Runtime、连接和设备占用状态");
            }

            if (save)
            {
                AppServices.Instance.SaveCameraParameters(descriptor, parameters);
            }
            ExposureStatusText.Text = save ? "曝光已设置并保存" : "曝光已设置（关闭相机后如需保留，请点击保存设置）";
        }
        catch (Exception ex)
        {
            ExposureStatusText.Text = $"设置失败：{ex.Message}";
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not CameraDescriptor descriptor)
        {
            ThemedMessageBox.Show("先扫描并选择设备", "提示");
            return;
        }
        if (_testInProgress)
        {
            return;
        }
        if (_cameraSession is not null || _cameraOperationInProgress)
        {
            TestStatusText.Text = "请先关闭持续开启的相机";
            return;
        }

        _testInProgress = true;
        TestButton.IsEnabled = false;
        _testCancellation = new CancellationTokenSource();
        var cancellationToken = _testCancellation.Token;
        TestStatusText.Text = $"打开 {descriptor.DisplayName}…";
        ICameraSession? session = null;
        try
        {
            session = await AppServices.Instance.Cameras.OpenSessionAsync(
                descriptor,
                AppServices.Instance.ApplyCameraDefaults(descriptor, new CameraOpenOptions()),
                cancellationToken);
            var options = AppServices.Instance.ApplyCameraDefaults(descriptor, new CameraOpenOptions());
            session.Faulted += (_, args) => Dispatcher.BeginInvoke(() => TestStatusText.Text = $"故障: {args.Fault.Message}");
            await session.OpenAsync(options, cancellationToken);
            if (session.State == CameraSessionState.Faulted)
            {
                throw new InvalidOperationException("相机打开失败，请检查设备是否被其他程序占用");
            }
            session.FrameReceived += (s, args) => Dispatcher.BeginInvoke(() =>
                _preview.Render(TestPreviewImage, args.Frame));
            await session.StartAsync(cancellationToken);
            _testSession = session;
            for (var remaining = 5; remaining > 0; remaining--)
            {
                TestButton.Content = $"测试选中设备（{remaining}）";
                TestStatusText.Text = $"预览中，{remaining} 秒后自动关闭…";
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            TestStatusText.Text = "测试完成";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TestStatusText.Text = "测试预览已关闭";
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"测试失败: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_testSession, session))
            {
                _testSession = null;
            }
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            _testCancellation?.Dispose();
            _testCancellation = null;
            _testInProgress = false;
            TestButton.Content = "测试选中设备";
            TestButton.IsEnabled = true;
        }
    }

    private async void CameraToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraSession is not null)
        {
            await StopCameraAsync();
            return;
        }
        await StartCameraAsync();
    }

    private async Task StartCameraAsync()
    {
        if (_cameraOperationInProgress || _testInProgress)
        {
            TestStatusText.Text = _testInProgress ? "请先等待测试预览结束" : "相机操作进行中";
            return;
        }
        if (DevicesGrid.SelectedItem is not CameraDescriptor descriptor)
        {
            ThemedMessageBox.Show("先扫描并选择设备", "提示");
            return;
        }

        _cameraOperationInProgress = true;
        CameraToggleButton.IsEnabled = false;
        ICameraSession? session = null;
        try
        {
            TestStatusText.Text = $"正在开启 {descriptor.DisplayName}…";
            var options = AppServices.Instance.ApplyCameraDefaults(descriptor, new CameraOpenOptions());
            session = await AppServices.Instance.Cameras.OpenSessionAsync(descriptor, options, CancellationToken.None);
            var activeSession = session;
            session.Faulted += (_, args) => Dispatcher.BeginInvoke(async () =>
            {
                if (ReferenceEquals(_cameraSession, activeSession))
                {
                    TestStatusText.Text = $"相机故障：{args.Fault.Message}";
                    await StopCameraAsync();
                }
            });
            await session.OpenAsync(options, CancellationToken.None);
            if (session.State == CameraSessionState.Faulted)
            {
                throw new InvalidOperationException("相机打开失败，请检查设备是否被其他程序占用");
            }
            session.FrameReceived += (_, args) => Dispatcher.BeginInvoke(() =>
                _preview.Render(TestPreviewImage, args.Frame));
            await session.StartAsync(CancellationToken.None);
            _cameraSession = session;
            session = null;
            CameraToggleButton.Content = "关闭相机";
            TestStatusText.Text = "相机已开启，正在持续预览";
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"开启相机失败：{ex.Message}";
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            _cameraOperationInProgress = false;
            CameraToggleButton.IsEnabled = true;
        }
    }

    private async Task StopCameraAsync()
    {
        if (_cameraOperationInProgress && _cameraSession is null)
        {
            return;
        }

        var session = _cameraSession;
        _cameraSession = null;
        CameraToggleButton.Content = "开启相机";
        if (session is null)
        {
            return;
        }

        CameraToggleButton.IsEnabled = false;
        try
        {
            await session.DisposeAsync();
            TestStatusText.Text = "相机已关闭";
        }
        finally
        {
            CameraToggleButton.IsEnabled = true;
        }
    }

}
