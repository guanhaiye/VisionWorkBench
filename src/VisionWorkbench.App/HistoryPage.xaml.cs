using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using VisionWorkbench.Application;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>历史记录页（文档 §8.4）：分页查询和原图/标注图。</summary>
public partial class HistoryPage : UserControl
{
    private sealed record TaskFilterItem(long? Id, string Name)
    {
        public long? Id { get; } = Id;
        public string Name { get; } = Name;

        public override string ToString() => Name;
    }

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!EnsureHistoryEnabled())
            {
                return;
            }
            if (TaskFilter.Items.Count == 0)
            {
                var tasks = await AppServices.Instance.Recipes.ListAsync();
                var items = new List<TaskFilterItem> { new(null, "全部任务") };
                items.AddRange(tasks.Select(t => new TaskFilterItem(t.Entity.Id, t.Recipe.Name)));
                TaskFilter.ItemsSource = items;
                TaskFilter.SelectedIndex = 0;
            }
            await Query();
        };
    }

    private void DatePicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DatePicker picker) return;
        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is DatePickerTextBox textBox)
        {
            // 日期只能通过日历按钮选择，禁用文本输入和键盘改值；日历本身仍可正常操作。
            textBox.IsReadOnly = true;
            textBox.Focusable = false;
            textBox.Cursor = Cursors.Arrow;
            textBox.PreviewKeyDown -= DatePickerTextBox_PreviewKeyDown;
            textBox.PreviewKeyDown += DatePickerTextBox_PreviewKeyDown;
            textBox.ContextMenu = CreateDateContextMenu(picker);
        }
        picker.ContextMenu = CreateDateContextMenu(picker);
    }

    private static void DatePickerTextBox_PreviewKeyDown(object sender, KeyEventArgs e) => e.Handled = true;

    private static ContextMenu CreateDateContextMenu(DatePicker picker)
    {
        var menu = new ContextMenu();
        var clear = new MenuItem { Header = "清空" };
        clear.Click += (_, _) => picker.SelectedDate = null;
        menu.Items.Add(clear);
        return menu;
    }

    private void DatePicker_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DatePicker picker) return;
        picker.ContextMenu ??= CreateDateContextMenu(picker);
        e.Handled = true;
        picker.ContextMenu.PlacementTarget = picker;
        picker.ContextMenu.IsOpen = true;
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHistoryEnabled())
        {
            return;
        }
        await Query();
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHistoryEnabled())
        {
            return;
        }
        try
        {
            var count = await AppServices.Instance.Records.CountAsync();
            if (count == 0)
            {
                ThemedMessageBox.Show("当前没有可清理的历史记录。", "历史记录",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmation = ThemedMessageBox.Show(
                $"确定清理全部 {count} 条历史记录吗？\n\n历史记录、原图、标注结果图和证据文件都会被删除，此操作不可恢复。任务、数据集和模型不会被删除。",
                "确认清理历史记录",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK)
            {
                return;
            }

            var paths = await AppServices.Instance.Records.DeleteAllAsync();
            var failedFiles = UiFileUtilities.DeleteFiles(paths);
            RecordsGrid.SelectedItem = null;
            OriginalImage.Source = null;
            AnnotatedImage.Source = null;
            DetailText.Text = "";
            await Query();
            ThemedMessageBox.Show(
                failedFiles.Count == 0
                    ? $"已清理 {count} 条历史记录及 {paths.Count} 个本地文件。"
                    : $"已清理 {count} 条历史记录，但有 {failedFiles.Count} 个本地文件未能删除，请检查文件是否被占用。",
                "历史记录",
                MessageBoxButton.OK,
                failedFiles.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"清理历史记录失败：{ex.Message}", "历史记录",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHistoryEnabled())
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"vision-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            var taskId = (TaskFilter.SelectedItem as TaskFilterItem)?.Id;
            var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content as string;
            await AppServices.Instance.Records.ExportCsvAsync(new RecordQuery(
                ProjectId: null,
                TaskId: taskId,
                StationCode: null,
                Status: status == "全部" ? null : status,
                From: FromDate.SelectedDate is { } from ? from.Date : null,
                To: ToDate.SelectedDate is { } to ? to.Date.AddDays(1).AddTicks(-1) : null,
                ModelVersion: null),
                dialog.FileName);
            ThemedMessageBox.Show("CSV 导出完成", "历史记录");
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"CSV 导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task Query()
    {
        if (!EnsureHistoryEnabled())
        {
            return;
        }
        var taskId = (TaskFilter.SelectedItem as TaskFilterItem)?.Id;
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content as string;
        var query = new RecordQuery(
            ProjectId: null,
            TaskId: taskId,
            StationCode: null,
            Status: status == "全部" ? null : status,
            From: FromDate.SelectedDate is { } d ? d.Date : null,
            To: ToDate.SelectedDate is { } t ? t.Date.AddDays(1).AddTicks(-1) : null,
            ModelVersion: null);
        var result = await AppServices.Instance.Records.QueryAsync(query, pageIndex: 0, pageSize: 500);
        RecordsGrid.ItemsSource = result.Items;
    }

    private bool EnsureHistoryEnabled()
    {
        if (AppServices.Instance.Settings.EnableHistory)
        {
            return true;
        }

        RecordsGrid.ItemsSource = Array.Empty<InspectionRecordEntity>();
        RecordsGrid.SelectedItem = null;
        OriginalImage.Source = null;
        AnnotatedImage.Source = null;
        DetailText.Text = "历史记录已关闭，请在系统设置中开启。";
        return false;
    }

    private void RecordsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not InspectionRecordEntity record)
        {
            return;
        }
        UiFileUtilities.ShowImage(OriginalImage, record.OriginalImagePath);
        UiFileUtilities.ShowImage(AnnotatedImage, record.AnnotatedImagePath);
        OriginalImageViewer.FitToWindow();
        ResultImageViewer.FitToWindow();
        DetailText.Text = $"记录 {record.Id} | 项目 {record.ProjectId} | 工位 {record.StationCode} | 任务 {record.TaskId} | 批次 {record.BatchId?.ToString() ?? "—"}\n"
            + $"{record.StartedAt:yyyy-MM-dd HH:mm:ss} → {(record.CompletedAt?.ToString("HH:mm:ss") ?? "—")}\n"
            + $"插件: {record.PluginVersion ?? "—"}\n"
            + $"判定: {record.FinalResultJson}"
            + FormatWorkflow(record);
    }

    private static string FormatWorkflow(InspectionRecordEntity record)
    {
        if (string.IsNullOrWhiteSpace(record.WorkflowResultJson))
        {
            return "\n模式: 普通视觉检测";
        }

        try
        {
            using var json = JsonDocument.Parse(record.WorkflowResultJson);
            var root = json.RootElement;
            var status = root.TryGetProperty("status", out var statusValue)
                ? statusValue.GetString() : null;
            var current = root.TryGetProperty("currentStepName", out var stepValue)
                ? stepValue.GetString() : null;
            return $"\n模式: SOP | 状态: {status ?? "—"} | 当前步骤: {current ?? "—"}";
        }
        catch (JsonException)
        {
            return "\n模式: SOP | 过程快照损坏";
        }
    }

}
