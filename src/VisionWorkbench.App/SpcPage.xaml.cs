using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VisionWorkbench.Application;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>SPC 结果图页：集中查看 NG/待确认检测的原图与标注结果图。</summary>
public partial class SpcPage : UserControl
{
    private int _pageIndex;
    private PagedResult<InspectionRecordEntity>? _lastResult;

    private sealed record TaskFilterItem(long? Id, string Name);

    public SpcPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                await LoadTasksAsync();
                await QueryAsync();
            }
            catch (Exception ex)
            {
                SummaryText.Text = "SPC 页面加载失败";
                ClearDetail($"SPC 页面加载失败：\n{ex.Message}");
            }
        };
    }

    private async Task LoadTasksAsync()
    {
        var tasks = await AppServices.Instance.Recipes.ListAsync();
        var items = new List<TaskFilterItem> { new(null, "全部任务") };
        items.AddRange(tasks.Select(t => new TaskFilterItem(t.Entity.Id, t.Recipe.Name)));
        TaskFilter.ItemsSource = items;
        TaskFilter.SelectedIndex = 0;
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        _pageIndex = 0;
        await QueryAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not InspectionRecordEntity record)
        {
            MessageBox.Show("请先选择要删除的检测记录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            $"确定删除记录 {record.Id} 吗？\n\n数据库记录以及对应的原图、标注结果图和证据文件都会被删除，此操作不可恢复。",
            "确认删除检测记录",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var paths = await AppServices.Instance.Records.DeleteAsync(record.Id);
            var failedFiles = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    failedFiles.Add(path);
                }
                catch (UnauthorizedAccessException)
                {
                    failedFiles.Add(path);
                }
            }

            await QueryAsync();
            SummaryText.Text = failedFiles.Count == 0
                ? $"记录 {record.Id} 及其 {paths.Count} 个本地文件已删除"
                : $"记录已删除，但有 {failedFiles.Count} 个文件未能删除，请检查文件是否被占用。";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "删除检测记录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = await AppServices.Instance.Records.CountAsync();
            if (count == 0)
            {
                MessageBox.Show("当前没有可清除的检测记录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmation = MessageBox.Show(
                $"确定清除全部 {count} 条检测记录吗？\n\n检测记录、原图、标注结果图和证据文件都会被删除，此操作不可恢复。\n任务、数据集和模型不会被删除。",
                "确认清除全部检测记录",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK)
            {
                return;
            }

            var paths = await AppServices.Instance.Records.DeleteAllAsync();
            var failedFiles = DeleteLocalFiles(paths);
            _pageIndex = 0;
            await QueryAsync();
            SummaryText.Text = failedFiles.Count == 0
                ? $"已清除 {count} 条检测记录及 {paths.Count} 个本地文件"
                : $"已清除 {count} 条检测记录，但有 {failedFiles.Count} 个文件未能删除，请检查文件是否被占用。";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清除全部失败：{ex.Message}", "清除检测记录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<string> DeleteLocalFiles(IReadOnlyList<string> paths)
    {
        var failedFiles = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                failedFiles.Add(path);
            }
            catch (UnauthorizedAccessException)
            {
                failedFiles.Add(path);
            }
        }

        return failedFiles;
    }

    private async Task QueryAsync()
    {
        try
        {
            var selectedStatus = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "evidence";
            var taskId = (TaskFilter.SelectedItem as TaskFilterItem)?.Id;
            var queryStatus = selectedStatus is "ng" or "review_required" or "ok"
                ? selectedStatus
                : null;
            _lastResult = await AppServices.Instance.Records.QueryAsync(
                new RecordQuery(
                    TaskId: taskId,
                    Status: queryStatus,
                    From: FromDate.SelectedDate?.Date,
                    To: ToDate.SelectedDate?.Date.AddDays(1).AddTicks(-1)),
                _pageIndex,
                pageSize: 100);

            var records = selectedStatus == "evidence"
                ? _lastResult.Items.Where(IsEvidenceRecord).ToArray()
                : _lastResult.Items.ToArray();
            RecordsGrid.ItemsSource = records;
            SummaryText.Text = $"查询到 {_lastResult.Total} 条记录，当前显示 {records.Length} 条";
            if (records.Length > 0)
            {
                RecordsGrid.SelectedIndex = 0;
            }
            else
            {
                ClearDetail("没有找到可显示的检测结果图。NG/待确认记录才会保存证据图。\n请先执行检测，或调整筛选条件。\n");
            }
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"查询失败：{ex.Message}";
            ClearDetail("查询失败，请检查数据库和筛选条件。\n" + ex.Message);
        }
    }

    private static bool IsEvidenceRecord(InspectionRecordEntity record)
        => record.Status is "ng" or "review_required"
            && !string.IsNullOrWhiteSpace(record.AnnotatedImagePath);

    private void RecordsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is InspectionRecordEntity record)
        {
            ShowRecord(record);
        }
    }

    private void RecordsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var row = ItemsControl.ContainerFromElement(RecordsGrid, source) as DataGridRow;
        if (row?.Item is InspectionRecordEntity record)
        {
            RecordsGrid.SelectedItem = record;
        }
    }

    private void ShowRecord(InspectionRecordEntity record)
    {
        ShowImage(AnnotatedImage, record.AnnotatedImagePath);
        ShowImage(OriginalImage, record.OriginalImagePath);
        DetailText.Text =
            $"记录 ID：{record.Id}\n"
            + $"状态：{StatusText(record.Status)}\n"
            + $"时间：{record.StartedAt:yyyy-MM-dd HH:mm:ss}\n"
            + $"项目：{record.ProjectId}\n"
            + $"工位：{record.StationCode}\n"
            + $"任务 ID：{record.TaskId}\n"
            + $"批次：{record.BatchId?.ToString() ?? "—"}\n"
            + $"算法耗时：{record.AlgorithmElapsedMs:0.#} ms\n"
            + $"总耗时：{record.TotalElapsedMs:0.#} ms\n"
            + $"已纠错：{(record.WasCorrected ? "是" : "否")}\n\n"
            + $"判定详情：\n{record.FinalResultJson}";
    }

    private static string StatusText(string status) => status switch
    {
        "ng" => "NG",
        "review_required" => "待确认",
        "ok" => "OK",
        _ => status,
    };

    private void ClearDetail(string message)
    {
        AnnotatedImage.Source = null;
        OriginalImage.Source = null;
        DetailText.Text = message;
    }

    private static void ShowImage(Image image, string? path)
    {
        try
        {
            image.Source = path is not null && File.Exists(path)
                ? new BitmapImage(new Uri(path))
                : null;
        }
        catch
        {
            image.Source = null;
        }
    }
}
