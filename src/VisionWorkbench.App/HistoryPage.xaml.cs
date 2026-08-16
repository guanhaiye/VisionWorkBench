using System.IO;
using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Application;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>历史记录页（文档 §8.4）：分页查询、原图/标注图、人工纠错。</summary>
public partial class HistoryPage : UserControl
{
    private int _pageIndex;
    private PagedResult<InspectionRecordEntity>? _lastResult;

    private sealed record TaskFilterItem(long? Id, string Name)
    {
        public long? Id { get; } = Id;
        public string Name { get; } = Name;
    }

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (TaskFilter.Items.Count == 0)
            {
                var tasks = await AppServices.Instance.Recipes.ListAsync();
                var items = new List<TaskFilterItem> { new(null, "全部任务") };
                items.AddRange(tasks.Select(t => new TaskFilterItem(t.Entity.Id, t.Recipe.Name)));
                TaskFilter.ItemsSource = items;
                TaskFilter.SelectedIndex = 0;
            }
            Query();
        };
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        _pageIndex = 0;
        await Query();
    }

    private async Task Query()
    {
        var taskId = (TaskFilter.SelectedItem as TaskFilterItem)?.Id;
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content as string;
        var query = new RecordQuery(
            TaskId: taskId,
            Status: status == "全部" ? null : status,
            From: FromDate.SelectedDate is { } d ? d.Date : null,
            To: ToDate.SelectedDate is { } t ? t.Date.AddDays(1).AddTicks(-1) : null);
        _lastResult = await AppServices.Instance.Records.QueryAsync(query, _pageIndex, pageSize: 50);
        RecordsGrid.ItemsSource = _lastResult.Items;
        PageText.Text = _lastResult.TotalPages == 0
            ? "0 / 0"
            : $"{_pageIndex + 1} / {_lastResult.TotalPages}（共 {_lastResult.Total} 条）";
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is { } r && _pageIndex > 0)
        {
            _pageIndex--;
            await Query();
        }
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is { } r && _pageIndex < r.TotalPages - 1)
        {
            _pageIndex++;
            await Query();
        }
    }

    private void RecordsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not InspectionRecordEntity record)
        {
            return;
        }
        ShowImage(OriginalImage, record.OriginalImagePath);
        ShowImage(AnnotatedImage, record.AnnotatedImagePath);
        DetailText.Text = $"记录 {record.Id} | 任务 {record.TaskId} | 批次 {record.BatchId?.ToString() ?? "—"}\n"
            + $"{record.StartedAt:yyyy-MM-dd HH:mm:ss} → {(record.CompletedAt?.ToString("HH:mm:ss") ?? "—")}\n"
            + $"插件: {record.PluginVersion ?? "—"} | 已纠错: {(record.WasCorrected ? "是" : "否")}\n"
            + $"判定: {record.FinalResultJson}";
    }

    private static void ShowImage(Image image, string? path)
    {
        try
        {
            image.Source = path is not null && File.Exists(path)
                ? new System.Windows.Media.Imaging.BitmapImage(new Uri(path))
                : null;
        }
        catch (Exception)
        {
            image.Source = null;
        }
    }

    private async void Correct_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not InspectionRecordEntity record)
        {
            MessageBox.Show("先选择一条记录", "提示");
            return;
        }
        var dialog = new CorrectionDialog($"纠错记录 {record.Id}（{record.Status}）", hideDelta: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            await AppServices.Instance.Records.AddCorrectionAsync(
                record.Id,
                beforeJson: record.FinalResultJson,
                afterJson: System.Text.Json.JsonSerializer.Serialize(new { corrected = true, @operator = dialog.OperatorName }),
                reason: dialog.Reason,
                operatorName: dialog.OperatorName);
            MessageBox.Show("纠错已保存", "历史记录");
            await Query();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"纠错失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void HistoryOfCorrections_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not InspectionRecordEntity record)
        {
            return;
        }
        var corrections = await AppServices.Instance.Records.ListCorrectionsAsync(record.Id);
        var text = corrections.Count == 0
            ? "无修正记录"
            : string.Join("\n\n", corrections.Select(c =>
                $"{c.CorrectedAt:yyyy-MM-dd HH:mm:ss} {c.OperatorName}\n原因: {c.Reason}"));
        MessageBox.Show(text, $"记录 {record.Id} 修正历史");
    }
}
