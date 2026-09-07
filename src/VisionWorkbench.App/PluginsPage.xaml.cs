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
        var discovered = svcs.AlgorithmManager.ScanPlugins();
        // 算法管理页只展示带有 plugin.json 的正式插件目录。
        // workers/.venv、.pytest_cache 等运行环境目录由扫描器记录诊断，但不是算法插件。
        var plugins = discovered
            .Where(p => File.Exists(Path.Combine(p.Directory, "plugin.json")))
            .ToArray();
        var pluginRoot = plugins.FirstOrDefault()?.Directory is { } pluginDirectory
            ? Directory.GetParent(pluginDirectory)?.FullName
            : null;
        PluginsRootText.Text = $"插件目录: {pluginRoot ?? "—"}";
        var rows = plugins.Select(p => new PluginRow(
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
            ThemedMessageBox.Show("先选择一个插件", "提示");
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
