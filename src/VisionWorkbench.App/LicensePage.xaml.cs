using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VisionWorkbench.App;

/// <summary>许可证管理页：展示离线授权状态并提供申请码、导入和诊断导出。</summary>
public partial class LicensePage : UserControl
{
    private readonly DispatcherTimer _toastTimer;

    public LicensePage()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            CopyToast.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => _toastTimer.Stop();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var services = AppServices.Instance;
        var status = services.License.Validate();
        var payload = status.Payload;

        StatusText.Text = status.State == "missing" ? "缺少许可证" : status.IsValid ? "已授权" : "授权无效";
        StatusMessageText.Text = status.Message;
        CheckedAtText.Text = $"状态：{status.State}；检查时间：{status.CheckedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        StatusBadge.Background = (Brush)FindResource(status.IsValid ? "SuccessBrush" : "DangerBrush");
        StatusText.Foreground = Brushes.White;

        CustomerText.Text = payload?.Customer ?? "—";
        ProductText.Text = payload is null ? "VisionWorkbench / —" : $"{payload.Product} / {payload.Edition}";
        LicenseIdText.Text = payload?.LicenseId ?? "—";
        ValidityText.Text = payload is null
            ? "—"
            : $"{payload.NotBeforeUtc.ToLocalTime():yyyy-MM-dd HH:mm} 至 {payload.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        MachineFingerprintText.Text = LicenseService.MachineFingerprint();
        FeaturesList.ItemsSource = payload?.Features?.Count > 0 ? payload.Features : ["未载入许可证功能"];
        LimitsText.Text = payload?.Limits is { Count: > 0 } limits
            ? "数量限制：" + string.Join("；", limits.Select(item => $"{item.Key}={item.Value}"))
            : "数量限制：无";
        RequestHintText.Text = "当前设备申请码可随时重新生成，复制后发送给授权方。";
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 VisionWorkbench 许可证",
            Filter = "许可证文件 (*.json;*.vwlicense)|*.json;*.vwlicense|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await AppServices.Instance.License.ImportAsync(dialog.FileName);
            Refresh();
            if (Window.GetWindow(this) is Shell shell) shell.ApplyLicenseAvailability(true);
            ThemedMessageBox.Show("许可证导入并校验成功。", "许可证", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Refresh();
            ThemedMessageBox.Show($"许可证导入失败：{ex.Message}", "许可证", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyActivationRequest_Click(object sender, RoutedEventArgs e) => CopyText(
        AppServices.Instance.License.CreateActivationRequestCode(), "授权申请码已复制");

    private void CopyText(string value, string message)
    {
        try
        {
            Clipboard.SetText(value);
            ShowToast(message);
        }
        catch (Exception ex)
        {
            ShowToast($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void ShowToast(string message, bool isError = false)
    {
        CopyToastText.Text = message;
        CopyToast.Background = (Brush)FindResource(isError ? "DangerBrush" : "AccentBrush");
        CopyToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出许可证诊断包",
            Filter = "诊断包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            FileName = $"visionworkbench-license-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            DiagnosticPackageService.Export(AppServices.Instance, dialog.FileName);
            RequestHintText.Text = "许可证诊断包导出完成（不含密码、私钥和客户原图）";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"诊断包导出失败：{ex.Message}", "许可证", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
