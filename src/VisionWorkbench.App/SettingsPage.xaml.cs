using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VisionWorkbench.App;

/// <summary>系统设置页（文档 §8.8）：数据目录、Python、插件目录、后端、角色（v1 免密）。</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        var s = AppServices.Instance.Settings;
        DataDirText.Text = s.DataDirectory;
        DatasetDirText.Text = string.IsNullOrWhiteSpace(s.DatasetDirectory) ? @"E:\" : s.DatasetDirectory;
        PythonText.Text = s.PythonExecutable ?? "";
        PluginsRootText2.Text = s.PluginsRoot ?? "";
        foreach (var item in BackendCombo.Items.OfType<ComboBoxItem>()
                     .Where(i => (string)i.Content == s.ExecutionProvider))
        {
            BackendCombo.SelectedItem = item;
        }
        foreach (var item in RoleCombo.Items.OfType<ComboBoxItem>()
                     .Where(i => (string?)i.Tag == s.CurrentRole))
        {
            RoleCombo.SelectedItem = item;
        }
        OperatorNameText.Text = s.OperatorName;
        EnableHistoryCheck.IsChecked = s.EnableHistory;
        ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), s.ThemeMode, StringComparison.OrdinalIgnoreCase));
        if (ThemeCombo.SelectedItem is null) ThemeCombo.SelectedIndex = 0;
        UiScaleCombo.SelectedItem = UiScaleCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => double.TryParse(item.Tag?.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var scale) && Math.Abs(scale - s.UiScale) < 0.001);
        if (UiScaleCombo.SelectedItem is null) UiScaleCombo.SelectedIndex = 2;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Instance.Settings;
        var newDataDir = DataDirText.Text.Trim();
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
        try
        {
            Path.GetFullPath(newDataDir);
            Path.GetFullPath(newDatasetDir);
        }
        catch (Exception)
        {
            ThemedMessageBox.Show("数据目录路径无效", "校验失败");
            return;
        }
        var dataDirChanged = !string.Equals(newDataDir, s.DataDirectory, StringComparison.OrdinalIgnoreCase);
        s.DataDirectory = newDataDir;
        s.DatasetDirectory = Path.GetFullPath(newDatasetDir);
        s.PythonExecutable = string.IsNullOrWhiteSpace(PythonText.Text.Trim()) ? null : PythonText.Text.Trim();
        s.PluginsRoot = string.IsNullOrWhiteSpace(PluginsRootText2.Text.Trim()) ? null : PluginsRootText2.Text.Trim();
        s.ExecutionProvider = (BackendCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "cpu";
        // 账号、角色和密码统一由登录与超级管理员用户管理维护，不能从系统设置绕过账号管控。
        s.EnableHistory = EnableHistoryCheck.IsChecked == true;
        s.ThemeMode = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "light";
        s.UiScale = double.TryParse((UiScaleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var uiScale)
            ? Math.Clamp(uiScale, 0.8, 1.5)
            : 1.0;
        AppServices.Instance.SaveUserSettings();
        ThemeManager.Apply(s.ThemeMode);
        ThemeManager.ApplyUiScale(s.UiScale);
        SaveHintText.Text = dataDirChanged ? "已保存（数据目录改动重启后生效）" : "已保存";
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
        }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "SQLite 备份 (*.db)|*.db|所有文件 (*.*)|*.*",
            FileName = $"visionworkbench-{DateTime.Now:yyyyMMdd-HHmmss}.db",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            AppServices.Instance.DatabaseBackup.BackupTo(dialog.FileName);
            SaveHintText.Text = "数据库备份完成";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"数据库备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQLite 备份 (*.db;*.sqlite)|*.db;*.sqlite|所有文件 (*.*)|*.*",
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
            AppServices.Instance.DatabaseBackup.RestoreFrom(dialog.FileName);
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
}
