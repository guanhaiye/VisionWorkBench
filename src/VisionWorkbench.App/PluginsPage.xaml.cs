using System.IO;
using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Contracts.Plugins;

namespace VisionWorkbench.App;

/// <summary>算法管理页（文档 §8.6）：插件扫描、状态、日志尾部。</summary>
public partial class PluginsPage : UserControl
{
    private sealed record PluginRow(
        string Id, string Version, string ProtocolVersion, string Runtime, string Capabilities, string Status);

    public PluginsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Scan();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Scan();

    private void Scan()
    {
        var svcs = AppServices.Instance;
        PluginsRootText.Text = $"插件目录: {svcs.AlgorithmManager.ScanPlugins().FirstOrDefault()?.Directory ?? "—"}";
        var rows = svcs.AlgorithmManager.ScanPlugins().Select(p => new PluginRow(
            p.Manifest?.Id ?? Path.GetFileName(p.Directory) ?? "?",
            p.Manifest?.Version ?? "—",
            p.Manifest?.ProtocolVersion ?? "—",
            p.Manifest?.Runtime?.Type ?? "—",
            p.Manifest is null ? "—" : string.Join(", ", p.Manifest.Capabilities.InputModes),
            p.Status.ToString()));
        PluginsGrid.ItemsSource = rows.ToArray();
    }

    private void TailLog_Click(object sender, RoutedEventArgs e)
    {
        if (PluginsGrid.SelectedItem is not PluginRow row || string.IsNullOrEmpty(row.Id))
        {
            MessageBox.Show("先选择一个插件", "提示");
            return;
        }
        var logsDir = Path.Combine(AppServices.Instance.Settings.DataDirectory, "logs");
        var invalid = Path.GetInvalidFileNameChars();
        var safeId = new string(row.Id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var logPath = Path.Combine(logsDir, $"plugin-{safeId}.log");
        if (!File.Exists(logPath))
        {
            LogTail.Text = $"暂无日志: {logPath}";
            return;
        }
        try
        {
            var lines = File.ReadLines(logPath).TakeLast(60);
            LogTail.Text = string.Join(Environment.NewLine, lines);
            LogTail.ScrollToEnd();
        }
        catch (IOException ex)
        {
            LogTail.Text = $"读取失败: {ex.Message}";
        }
    }
}
