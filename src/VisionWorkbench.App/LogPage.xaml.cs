using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VisionWorkbench.App;

/// <summary>日志查看页：汇总应用、相机、算法、数据库和插件日志，并支持等级筛选。</summary>
public partial class LogPage : UserControl
{
    private static readonly Regex CurrentLogHeaderRegex = new(
        @"^(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:\s+[+-]\d{2}:\d{2})?)\s+\[(?<level>VRB|DBG|INF|WRN|ERR|FTL)\]\s?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyLogHeaderRegex = new(
        @"^\[(?<time>[^\]]+)\s+(?<level>VRB|DBG|INF|WRN|ERR|FTL)\]\s?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private IReadOnlyList<LogEntry> _entries = [];
    private string _logDirectory = "";

    private sealed record LogEntry(
        DateTime Timestamp,
        string LevelKey,
        string Level,
        string Source,
        string Message,
        string FileName,
        string FilePath,
        int StartLine,
        int EndLine);

    public LogPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshLogs();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLogs();

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void LogsGrid_PreviewMouseRightButtonDown(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var row = ItemsControl.ContainerFromElement(LogsGrid, source) as DataGridRow;
        if (row?.Item is LogEntry entry)
        {
            // 先选中右键所在行，保留 WPF 默认高亮，菜单操作对象明确可见。
            LogsGrid.SelectedItem = entry;
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LogsGrid.SelectedItem is not LogEntry entry)
        {
            ThemedMessageBox.Show("请先右键点击一条日志记录。", "日志管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = ThemedMessageBox.Show(
            $"确定删除这条日志吗？\n\n时间：{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\n内容：{entry.Message}",
            "确认删除日志",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            DeleteEntry(entry);
            RefreshLogs();
            SummaryText.Text = "已删除当前日志记录";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"删除日志失败：{ex.Message}", "日志管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSelectedFile_Click(object sender, RoutedEventArgs e)
    {
        if (LogsGrid.SelectedItem is not LogEntry entry)
        {
            ThemedMessageBox.Show("请先右键点击一条日志记录。", "日志管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!File.Exists(entry.FilePath))
            {
                ThemedMessageBox.Show("对应的日志文件不存在，请先刷新日志列表。", "日志管理",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"无法打开日志文件：{ex.Message}", "日志管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logDirectory))
            {
                RefreshLogs();
            }

            var files = Directory.Exists(_logDirectory)
                ? Directory.EnumerateFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly).ToArray()
                : [];
            if (files.Length == 0)
            {
                ThemedMessageBox.Show("当前没有可清除的日志。", "日志管理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmation = ThemedMessageBox.Show(
                $"确定清除全部日志吗？\n\n将清空 {files.Length} 个日志文件的内容，但保留文件本身，程序后续仍会继续记录日志。此操作不可恢复。",
                "确认清除全部日志",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK)
            {
                return;
            }

            var failedFiles = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    stream.SetLength(0);
                }
                catch (IOException)
                {
                    failedFiles.Add(file);
                }
                catch (UnauthorizedAccessException)
                {
                    failedFiles.Add(file);
                }
            }

            RefreshLogs();
            SummaryText.Text = failedFiles.Count == 0
                ? $"已清除全部 {files.Length} 个日志文件"
                : $"已清除日志，但有 {failedFiles.Count} 个文件无法访问";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"清除全部日志失败：{ex.Message}", "日志管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshLogs()
    {
        try
        {
            _logDirectory = Path.Combine(AppServices.Instance.Settings.DataDirectory, "logs");
            LogPathText.Text = $"目录：{_logDirectory}";
            _entries = ReadEntries(_logDirectory);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _entries = [];
            LogsGrid.ItemsSource = null;
            SummaryText.Text = $"读取日志失败：{ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        if (LogsGrid is null || SummaryText is null)
        {
            return;
        }

        var level = (LevelFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var keyword = SearchText.Text.Trim();
        var filtered = _entries.Where(entry =>
                (level == "all" || entry.LevelKey == level)
                && (keyword.Length == 0
                    || entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || entry.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || entry.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Timestamp)
            .ToArray();

        LogsGrid.ItemsSource = filtered;
        SummaryText.Text = $"共读取 {_entries.Count} 条日志，当前显示 {filtered.Length} 条";
    }

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logDirectory))
            {
                RefreshLogs();
            }
            Directory.CreateDirectory(_logDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _logDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"无法打开日志目录：{ex.Message}", "日志管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (LogsGrid.ItemsSource is not IEnumerable<LogEntry> entries)
        {
            ThemedMessageBox.Show("当前没有可导出的日志。", "日志管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"visionworkbench-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var lines = entries.Select(entry =>
                $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Source}] {entry.Message}");
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ThemedMessageBox.Show("当前筛选结果已导出。", "日志管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"导出日志失败：{ex.Message}", "日志管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<LogEntry> ReadEntries(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var entries = new List<LogEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            entries.AddRange(ReadEntriesFromFile(path));
        }
        return entries;
    }

    private static List<LogEntry> ReadEntriesFromFile(string path)
    {
        var lines = ReadLinesShared(path).ToArray();
        var fileName = Path.GetFileName(path);
        var source = SourceName(fileName);
        var entries = new List<LogEntry>();
        LogEntry? current = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (TryParseHeader(line, out var timestamp, out var levelKey, out var message))
            {
                if (current is not null)
                {
                    entries.Add(current with { EndLine = index });
                }
                current = new LogEntry(timestamp, levelKey, DisplayLevel(levelKey), source, message,
                    fileName, path, index, lines.Length);
            }
            else if (current is not null && line.Length > 0)
            {
                current = current with { Message = current.Message + Environment.NewLine + line };
            }
        }
        if (current is not null)
        {
            entries.Add(current with { EndLine = lines.Length });
        }
        return entries;
    }

    private static void DeleteEntry(LogEntry entry)
    {
        if (!File.Exists(entry.FilePath))
        {
            throw new FileNotFoundException("日志文件不存在", entry.FilePath);
        }

        var lines = ReadLinesShared(entry.FilePath).ToArray();
        var currentEntries = ReadEntriesFromLines(entry.FilePath, lines);
        var current = currentEntries.FirstOrDefault(candidate =>
            candidate.StartLine == entry.StartLine
            && candidate.LevelKey == entry.LevelKey
            && candidate.Message == entry.Message);
        if (current is null)
        {
            throw new InvalidOperationException("日志文件已发生变化，请刷新后重试。");
        }

        var retained = lines
            .Where((_, index) => index < current.StartLine || index >= current.EndLine)
            .ToArray();
        WriteLinesShared(entry.FilePath, retained);
    }

    private static List<LogEntry> ReadEntriesFromLines(string path, IReadOnlyList<string> lines)
    {
        var fileName = Path.GetFileName(path);
        var source = SourceName(fileName);
        var entries = new List<LogEntry>();
        LogEntry? current = null;
        for (var index = 0; index < lines.Count; index++)
        {
            if (TryParseHeader(lines[index], out var timestamp, out var levelKey, out var message))
            {
                if (current is not null)
                {
                    entries.Add(current with { EndLine = index });
                }
                current = new LogEntry(timestamp, levelKey, DisplayLevel(levelKey), source, message,
                    fileName, path, index, lines.Count);
            }
            else if (current is not null && lines[index].Length > 0)
            {
                current = current with { Message = current.Message + Environment.NewLine + lines[index] };
            }
        }
        if (current is not null)
        {
            entries.Add(current with { EndLine = lines.Count });
        }
        return entries;
    }

    private static void WriteLinesShared(string path, IEnumerable<string> lines)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool TryParseHeader(string line, out DateTime timestamp, out string levelKey, out string message)
    {
        var match = CurrentLogHeaderRegex.Match(line);
        if (!match.Success)
        {
            match = LegacyLogHeaderRegex.Match(line);
        }
        if (!match.Success)
        {
            timestamp = default;
            levelKey = "debug";
            message = "";
            return false;
        }

        var rawTime = match.Groups["time"].Value.Trim();
        timestamp = DateTime.Now;
        if (DateTime.TryParse(rawTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsedDate))
        {
            timestamp = parsedDate;
        }
        else if (TimeSpan.TryParse(rawTime, CultureInfo.InvariantCulture, out var parsedTime))
        {
            timestamp = DateTime.Today.Add(parsedTime);
        }

        levelKey = match.Groups["level"].Value switch
        {
            "VRB" or "DBG" => "debug",
            "INF" => "information",
            "WRN" => "warning",
            "ERR" => "error",
            "FTL" => "critical",
            _ => "debug",
        };
        message = match.Groups["message"].Value;
        return true;
    }

    private static string DisplayLevel(string levelKey) => levelKey switch
    {
        "debug" => "详细",
        "information" => "一般",
        "warning" => "警告",
        "error" => "报错",
        "critical" => "严重",
        _ => levelKey,
    };

    private static string SourceName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name switch
        {
            "application" => "应用",
            "camera" => "相机",
            "algorithm-manager" => "算法管理",
            "database" => "数据库",
            _ when name.StartsWith("plugin-", StringComparison.OrdinalIgnoreCase) => "插件：" + name[7..],
            _ => name,
        };
    }
}
