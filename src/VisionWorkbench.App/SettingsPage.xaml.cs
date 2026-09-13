using System.IO;
using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VisionWorkbench.Infrastructure.Logging;

namespace VisionWorkbench.App;

/// <summary>系统设置页（文档 §8.8）：数据目录、Python、插件目录、后端、角色（v1 免密）。</summary>
public partial class SettingsPage : UserControl
{
    private sealed record BackupVersionView(string Path, string DisplayText);
    private bool _isLoading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer outerScrollViewer)
        {
            return;
        }

        // Nested controls such as the backup-version list can consume the wheel
        // event even after they reach an edge. Continue scrolling the settings
        // page in that case instead of making the pointer feel stuck.
        var innerScrollViewer = FindNearestScrollViewer(e.OriginalSource as DependencyObject);
        if (innerScrollViewer is not null && !ReferenceEquals(innerScrollViewer, outerScrollViewer))
        {
            var canScrollInner = e.Delta > 0
                ? innerScrollViewer.VerticalOffset > 0
                : innerScrollViewer.VerticalOffset < innerScrollViewer.ScrollableHeight;
            if (canScrollInner)
            {
                return;
            }
        }

        var targetOffset = outerScrollViewer.VerticalOffset - e.Delta;
        outerScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(targetOffset, 0, outerScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private static ScrollViewer? FindNearestScrollViewer(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void Load()
    {
        _isLoading = true;
        try
        {
            var s = AppServices.Instance.Settings;
            DataDirText.Text = s.DataDirectory;
            SopRecordingDirText.Text = s.SopRecordingDirectory;
            DatasetDirText.Text = string.IsNullOrWhiteSpace(s.DatasetDirectory) ? @"E:\" : s.DatasetDirectory;
            PythonText.Text = s.PythonExecutable ?? "";
            PluginsRootText2.Text = s.PluginsRoot ?? "";
            foreach (var item in BackendCombo.Items.OfType<ComboBoxItem>()
                         .Where(i => (string)i.Content == s.ExecutionProvider))
            {
                BackendCombo.SelectedItem = item;
            }
            var currentRole = s.CurrentRole?.Trim().ToLowerInvariant();
            RoleCombo.SelectedItem = RoleCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                {
                    var itemRole = item.Tag?.ToString();
                    return string.Equals(itemRole, currentRole, StringComparison.OrdinalIgnoreCase)
                        || (itemRole == "admin" && currentRole == "superadmin");
                });
            OperatorNameText.Text = s.OperatorName;
            EnableHistoryCheck.IsChecked = s.EnableHistory;
            LogPersistenceLevelCombo.SelectedItem = LogPersistenceLevelCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), s.LogPersistenceLevel.ToString(), StringComparison.Ordinal));
            if (LogPersistenceLevelCombo.SelectedItem is null) LogPersistenceLevelCombo.SelectedIndex = 0;
            ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), s.ThemeMode, StringComparison.OrdinalIgnoreCase));
            if (ThemeCombo.SelectedItem is null) ThemeCombo.SelectedIndex = 0;
            UiScaleCombo.SelectedItem = UiScaleCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => double.TryParse(item.Tag?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var scale) && Math.Abs(scale - s.UiScale) < 0.001);
            if (UiScaleCombo.SelectedItem is null) UiScaleCombo.SelectedIndex = 2;
            AutomaticBackupCheck.IsChecked = s.AutomaticBackupEnabled;
            AutomaticBackupIntervalCombo.SelectedItem = AutomaticBackupIntervalCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), s.AutomaticBackupIntervalMinutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (AutomaticBackupIntervalCombo.SelectedItem is null)
            {
                AutomaticBackupIntervalCombo.SelectedIndex = 0;
                s.AutomaticBackupIntervalMinutes = 1440;
            }
            AutomaticBackupRetentionCombo.SelectedItem = AutomaticBackupRetentionCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), s.AutomaticBackupRetentionCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (AutomaticBackupRetentionCombo.SelectedItem is null) AutomaticBackupRetentionCombo.SelectedIndex = 2;
            UpdateBackupScheduleText();
            RefreshBackupVersions();
            var license = AppServices.Instance.License.Current;
            LicenseStatusText.Text = $"状态：{license.State}；{license.Message}\n许可证路径：{AppServices.Instance.License.LicensePath}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void AutoSaveTextBox_LostFocus(object sender, RoutedEventArgs e) => AutoSaveSettings();

    private void AutoSaveSetting_Changed(object sender, RoutedEventArgs e) => AutoSaveSettings();

    private void AutoSaveSelectionChanged(object sender, SelectionChangedEventArgs e) => AutoSaveSettings();

    private void AutoSaveSettings()
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        var s = AppServices.Instance.Settings;
        var newDataDir = DataDirText.Text.Trim();
        var newSopRecordingDir = SopRecordingDirText.Text.Trim();
        var newDatasetDir = DatasetDirText.Text.Trim();
        if (string.IsNullOrWhiteSpace(newDataDir))
        {
            ThemedMessageBox.Show("数据目录不能为空", "校验失败");
            return;
        }
        if (string.IsNullOrWhiteSpace(newDatasetDir))
        {
            ThemedMessageBox.Show("标注数据集目录不能为空", "校验失败");
            return;
        }
        if (string.IsNullOrWhiteSpace(newSopRecordingDir))
        {
            ThemedMessageBox.Show("SOP 采集数据目录不能为空", "校验失败");
            return;
        }
        try
        {
            Path.GetFullPath(newDataDir);
            newSopRecordingDir = Path.GetFullPath(newSopRecordingDir);
            Path.GetFullPath(newDatasetDir);
            Directory.CreateDirectory(newSopRecordingDir);
        }
        catch (Exception)
        {
            ThemedMessageBox.Show("数据目录路径无效", "校验失败");
            return;
        }
        var dataDirChanged = !string.Equals(newDataDir, s.DataDirectory, StringComparison.OrdinalIgnoreCase);
        var sopRecordingDirChanged = !string.Equals(
            newSopRecordingDir, s.SopRecordingDirectory, StringComparison.OrdinalIgnoreCase);
        s.DataDirectory = newDataDir;
        s.SopRecordingDirectory = newSopRecordingDir;
        s.DatasetDirectory = Path.GetFullPath(newDatasetDir);
        s.PythonExecutable = string.IsNullOrWhiteSpace(PythonText.Text.Trim()) ? null : PythonText.Text.Trim();
        s.PluginsRoot = string.IsNullOrWhiteSpace(PluginsRootText2.Text.Trim()) ? null : PluginsRootText2.Text.Trim();
        s.ExecutionProvider = (BackendCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "cpu";
        // 账号、角色和密码统一由登录与超级管理员用户管理维护，不能从系统设置绕过账号管控。
        s.EnableHistory = EnableHistoryCheck.IsChecked == true;
        if (LogPersistenceLevelCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<LogPersistenceLevel>(tag, ignoreCase: false, out var logLevel))
        {
            s.LogPersistenceLevel = logLevel;
        }
        s.ThemeMode = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "light";
        s.UiScale = double.TryParse((UiScaleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var uiScale)
            ? Math.Clamp(uiScale, 0.8, 1.5)
            : 1.0;
        if (!ReadAutomaticBackupSettings()) return;
        AppServices.Instance.SaveUserSettings();
        ThemeManager.Apply(s.ThemeMode);
        ThemeManager.ApplyUiScale(s.UiScale);
        SaveHintText.Text = dataDirChanged
            ? "已保存（数据目录改动重启后生效）"
            : sopRecordingDirChanged
                ? "已保存（SOP 采集目录已更新，新录制立即使用）"
                : "已保存";
    }

    private bool ReadAutomaticBackupSettings()
    {
        var s = AppServices.Instance.Settings;
        if (!int.TryParse((AutomaticBackupIntervalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMinutes)
            || !int.TryParse((AutomaticBackupRetentionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionCount))
        {
            ThemedMessageBox.Show("请选择有效的自动备份频率和保留数量", "校验失败");
            return false;
        }

        s.AutomaticBackupEnabled = AutomaticBackupCheck.IsChecked == true;
        s.AutomaticBackupIntervalMinutes = Math.Clamp(intervalMinutes, 1440, 43200);
        s.AutomaticBackupRetentionCount = Math.Clamp(retentionCount, 1, 365);
        AppServices.Instance.AutomaticBackups.Configure(
            s.AutomaticBackupEnabled,
            s.AutomaticBackupIntervalMinutes,
            s.AutomaticBackupRetentionCount);
        UpdateBackupScheduleText();
        return true;
    }

    private void UpdateBackupScheduleText()
    {
        var s = AppServices.Instance.Settings;
        BackupScheduleText.Text = s.AutomaticBackupEnabled
            ? $"自动备份已启用：{FormatInterval(s.AutomaticBackupIntervalMinutes)}执行一次，最多保留 {s.AutomaticBackupRetentionCount} 个版本。备份不包含原始图片和临时缓存。"
            : "自动备份已停用。仍可使用“立即备份”创建版本，或使用下方已有版本恢复。";
    }

    private static string FormatInterval(int minutes) => minutes switch
    {
        1440 => "每天",
        10080 => "每周",
        43200 => "每月",
        _ when minutes % 43200 == 0 => $"每 {minutes / 43200} 个月",
        _ when minutes % 1440 == 0 => $"每 {minutes / 1440} 天",
        _ => $"每 {minutes} 分钟",
    };

    private async void CreateLocalBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AppServices.Instance.AutomaticBackups.RunNowAsync();
            RefreshBackupVersions();
            BackupHintText.Text = "参数备份已创建，并已完成备份文件校验。";
        }
        catch (Exception ex)
        {
            BackupHintText.Text = $"参数备份失败：{ex.Message}";
        }
    }

    private void RefreshBackups_Click(object sender, RoutedEventArgs e) => RefreshBackupVersions();

    private void RefreshBackupVersions()
    {
        try
        {
            var directory = AppServices.Instance.AutomaticBackups.BackupDirectory;
            Directory.CreateDirectory(directory);
            var versions = Directory.EnumerateFiles(directory, "*.vwbackup", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new BackupVersionView(
                    file.FullName,
                    $"{file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  |  {GetBackupKind(file.Name)}  |  {FormatBytes(file.Length)}"))
                .ToList();
            BackupVersionsList.ItemsSource = versions;
            BackupHintText.Text = versions.Count == 0
                ? "当前还没有参数备份版本。"
                : $"共 {versions.Count} 个备份版本；恢复前程序会自动再创建一个当前状态备份。";
        }
        catch (Exception ex)
        {
            BackupHintText.Text = $"读取备份版本失败：{ex.Message}";
        }
    }

    private static string GetBackupKind(string fileName) => fileName.StartsWith("automatic-", StringComparison.OrdinalIgnoreCase)
        ? "自动备份"
        : fileName.StartsWith("pre-restore-", StringComparison.OrdinalIgnoreCase) ? "恢复前保护备份" : "手动备份";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B",
    };

    private void OpenBackupDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = AppServices.Instance.AutomaticBackups.BackupDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            BackupHintText.Text = $"打开备份目录失败：{ex.Message}";
        }
    }

    private async void RestoreSelectedBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupVersionsList.SelectedItem is not BackupVersionView selected)
        {
            ThemedMessageBox.Show("请先选择要恢复的备份版本", "恢复备份");
            return;
        }
        if (ThemedMessageBox.Show(
                $"确定恢复以下版本吗？\n\n{selected.DisplayText}\n\n恢复前会自动创建当前状态备份，恢复完成后需要重启软件。",
                "确认恢复参数", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await AppServices.Instance.BackupPackages.RestoreAsync(selected.Path);
            BackupHintText.Text = "参数已恢复，请重启软件使全部配置生效。";
            RefreshBackupVersions();
        }
        catch (Exception ex)
        {
            BackupHintText.Text = $"参数恢复失败：{ex.Message}";
        }
    }

    private void BrowseDatasetDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择标注数据集存储目录",
            InitialDirectory = Directory.Exists(DatasetDirText.Text) ? DatasetDirText.Text : @"E:\",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            DatasetDirText.Text = dialog.FolderName;
            AutoSaveSettings();
        }
    }

    private void BrowseSopRecordingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(SopRecordingDirText.Text)
            ? SopRecordingDirText.Text
            : AppServices.Instance.Settings.SopRecordingDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = "选择 SOP 采集数据目录",
            InitialDirectory = Directory.Exists(initialDirectory)
                ? initialDirectory
                : AppServices.Instance.Settings.DataDirectory,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            SopRecordingDirText.Text = dialog.FolderName;
            AutoSaveSettings();
        }
    }

    private async void ImportLicense_Click(object sender, RoutedEventArgs e)
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
            var status = await AppServices.Instance.License.ImportAsync(dialog.FileName);
            LicenseStatusText.Text = $"状态：{status.State}；{status.Message}\n许可证路径：{AppServices.Instance.License.LicensePath}";
            await AppServices.Instance.Audit.RecordAsync("license.import", "license", status.Payload?.LicenseId,
                AppServices.Instance.Settings.OperatorName);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"许可证导入失败：{ex.Message}", "许可证", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "VisionWorkbench 完整备份 (*.vwbackup)|*.vwbackup|SQLite 备份（兼容） (*.db)|*.db|所有文件 (*.*)|*.*",
            FileName = $"visionworkbench-{DateTime.Now:yyyyMMdd-HHmmss}.vwbackup",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".db", StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Instance.DatabaseBackup.BackupTo(dialog.FileName);
                SaveHintText.Text = "数据库备份完成（兼容格式）";
            }
            else
            {
                var result = await AppServices.Instance.BackupPackages.CreateAsync(
                    dialog.FileName, AppServices.Instance.SettingsFile, AppServices.Instance.Settings.DataDirectory);
                SaveHintText.Text = $"完整备份完成，已校验 SHA-256：{result.Sha256[..12]}…";
                await AppServices.Instance.Audit.RecordAsync("backup.create", "backup", dialog.FileName,
                    AppServices.Instance.Settings.OperatorName, detailsJson: $"{{\"sha256\":\"{result.Sha256}\"}}");
            }
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"数据库备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "VisionWorkbench 完整备份 (*.vwbackup)|*.vwbackup|SQLite 备份（兼容） (*.db;*.sqlite)|*.db;*.sqlite|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        if (ThemedMessageBox.Show("恢复会覆盖当前数据库，确认继续吗？恢复后请重启应用。",
                "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".vwbackup", StringComparison.OrdinalIgnoreCase))
            {
                await AppServices.Instance.BackupPackages.RestoreAsync(dialog.FileName);
            }
            else
            {
                AppServices.Instance.DatabaseBackup.RestoreFrom(dialog.FileName);
            }
            await AppServices.Instance.Audit.RecordAsync("backup.restore", "backup", dialog.FileName,
                AppServices.Instance.Settings.OperatorName);
            SaveHintText.Text = "数据库已恢复，请重启应用";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"数据库恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "诊断包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            FileName = $"visionworkbench-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            DiagnosticPackageService.Export(AppServices.Instance, dialog.FileName);
            SaveHintText.Text = "诊断包导出完成（不含客户图片）";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"诊断包导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SelfCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = await CommercialSelfCheckService.RunAsync(AppServices.Instance);
            SelfCheckText.Text = $"自检结果：{report.OverallState.ToUpperInvariant()}\n" +
                string.Join("\n", report.Items.Select(item => $"[{item.State}] {item.Code} {item.Summary}" +
                    (string.IsNullOrWhiteSpace(item.Recommendation) ? "" : $"；建议：{item.Recommendation}")));
        }
        catch (Exception ex)
        {
            SelfCheckText.Text = $"自检失败：{ex.Message}";
        }
    }
}
