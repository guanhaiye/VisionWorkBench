using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.Logging;

using VisionWorkbench.Application;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

public partial class LivePage : UserControl
{
    private readonly List<LiveTaskPanel> _panels = [];
    private LiveTaskItem[] _availableTasks = [];
    private bool _loadingTasks;
    private bool _runtimeMode;
    // 兼容原有 UI 冒烟测试；正式运行时每个 LiveTaskPanel 使用自己的画布。
    private Recipe? _activeRecipe = null;
    private DetectionRunService? _run = null;

    public LivePage()
    {
        InitializeComponent();
        LayoutCombo.SelectedIndex = AppServices.Instance.Settings.LiveLayout switch
        {
            "vertical" => 1,
            "horizontal" => 2,
            _ => 0,
        };
        ApplyLayout();
    }

    public void OnShown()
    {
        _ = LoadTasksAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadTasksAsync();
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        SetRuntimeMode(false);
        SaveWorkspaceSettings();
        await ShutdownPanelsAsync();
    }

    private async Task LoadTasksAsync()
    {
        if (_loadingTasks)
        {
            return;
        }

        _loadingTasks = true;
        try
        {
            var tasks = await AppServices.Instance.Recipes.ListAsync();
            var items = tasks
                .Where(t => t.Recipe.Sop is null
                    || t.Recipe.Sop.Definition?.Status == SopDefinitionStatus.Published)
                .Select(t => new LiveTaskItem(t.Entity.Id, t.Recipe.StationCode, t.Recipe.Name))
                .ToArray();
            _availableTasks = items;
            foreach (var panel in _panels)
            {
                panel.SetAvailableTasks(_availableTasks);
            }

            if (_panels.Count == 0)
            {
                var restoredTaskIds = (AppServices.Instance.Settings.LiveTaskIds ?? [])
                    .Where(id => id is null || items.Any(item => item.Id == id.Value))
                    .ToArray();
                if (restoredTaskIds.Length == 0)
                {
                    AddTaskPanel();
                }
                else
                {
                    foreach (var taskId in restoredTaskIds)
                    {
                        if (taskId is null || items.Any(item => item.Id == taskId.Value))
                        {
                            AddTaskPanel(taskId);
                        }
                    }
                }
            }
            else
            {
                UpdateWorkspaceStatus();
            }

            await PrepareModelsAsync();
        }
        catch (Exception ex)
        {
            AppServices.Instance.LoggerFactory
                .CreateLogger<LivePage>()
                .LogError(ex, "实时检测加载任务失败");
            WorkspaceStatusText.Text = $"加载任务失败：{ex.Message}";
        }
        finally
        {
            _loadingTasks = false;
        }
    }

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (_availableTasks.Length == 0)
        {
            WorkspaceStatusText.Text = "暂无可添加的任务，请先在任务配置中创建任务。";
            return;
        }

        AddTaskPanel();
    }

    private void WorkspaceSettings_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceSettingsPopup.IsOpen = !WorkspaceSettingsPopup.IsOpen;
    }

    private void RuntimeMode_Click(object sender, RoutedEventArgs e)
    {
        SetRuntimeMode(!_runtimeMode);
        WorkspaceSettingsPopup.IsOpen = false;
    }

    private void SetRuntimeMode(bool enabled)
    {
        _runtimeMode = enabled;
        RuntimeModeButton.Content = enabled ? "退出运行模式" : "进入运行模式";
        if (Window.GetWindow(this) is Shell shell)
        {
            shell.SetLiveRuntimeMode(enabled);
        }
        foreach (var panel in _panels)
        {
            panel.SetRuntimeMode(enabled);
        }
        WorkspaceStatusText.Text = enabled
            ? "已进入运行模式，左侧工具栏已隐藏。可在“设置”中退出运行模式。"
            : "已退出运行模式，左侧工具栏已恢复。";
    }

    private void AddTaskPanel(long? taskId = null)
    {
        var panel = new LiveTaskPanel(_availableTasks);
        panel.RemoveRequested += Panel_RemoveRequested;
        panel.TaskSelectionChanged += Panel_TaskSelectionChanged;
        panel.SettingsChanged += Panel_SettingsChanged;
        panel.SetRuntimeMode(_runtimeMode);
        _panels.Add(panel);
        RefreshPanelNumbers();
        PanelsHost.Items.Add(panel);
        ApplyLayout();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(ApplyLayout));
        if (taskId is { } id)
        {
            panel.SelectTask(id);
        }
        SaveWorkspaceSettings();
        UpdateWorkspaceStatus();
    }

    private void Panel_TaskSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not LiveTaskPanel panel || panel.TaskId == 0)
        {
            UpdateWorkspaceStatus();
            return;
        }

        var duplicate = _panels.Any(other => !ReferenceEquals(other, panel)
            && other.TaskId == panel.TaskId);
        if (duplicate)
        {
            panel.ClearTaskSelection();
            WorkspaceStatusText.Text = $"任务 {panel.SelectedTaskName} 已经被其他检测面板使用，请选择其他任务。";
            return;
        }

        RestorePanelRoi(panel);
        SaveWorkspaceSettings();
        UpdateWorkspaceStatus();
    }

    private void Panel_SettingsChanged(object? sender, EventArgs e)
    {
        SaveWorkspaceSettings();
    }

    private async void Panel_RemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not LiveTaskPanel panel)
        {
            return;
        }

        var taskName = panel.TaskId == 0 ? "当前检测面板" : $"任务「{panel.SelectedTaskName}」";
        var confirmation = new ConfirmDialog(
            "确认移除",
            $"确定要移除{taskName}吗？\n正在运行的检测会被停止。")
        {
            Owner = Window.GetWindow(this)
        };
        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        panel.RemoveRequested -= Panel_RemoveRequested;
        panel.TaskSelectionChanged -= Panel_TaskSelectionChanged;
        panel.SettingsChanged -= Panel_SettingsChanged;
        await panel.ShutdownAsync();
        _panels.Remove(panel);
        RefreshPanelNumbers();
        PanelsHost.Items.Remove(panel);
        SaveWorkspaceSettings();
        UpdateWorkspaceStatus();
    }

    private void RefreshPanelNumbers()
    {
        for (var index = 0; index < _panels.Count; index++)
        {
            _panels[index].SetPanelNumber(index + 1);
        }
    }

    private async void RefreshTasks_Click(object sender, RoutedEventArgs e)
    {
        await LoadTasksAsync();
    }

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && IsLoaded)
        {
            ApplyLayout();
            SaveWorkspaceSettings();
        }
    }

    private void PanelsViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyLayout();
        }
    }

    private void SaveWorkspaceSettings()
    {
        var settings = AppServices.Instance.Settings;
        settings.LiveLayout = (LayoutCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "grid";
        // 按面板顺序保存；null 表示该面板尚未选择任务，避免重启后丢失空面板。
        settings.LiveTaskIds = _panels
            .Select(panel => panel.TaskId > 0 ? panel.TaskId : (long?)null)
            .ToList();
        settings.LiveRois ??= [];
        foreach (var panel in _panels.Where(panel => panel.TaskId > 0 && panel.HasRoiOverride))
        {
            settings.LiveRois[panel.TaskId] = panel.RoiOverride;
        }
        try
        {
            AppServices.Instance.SaveUserSettings();
        }
        catch (Exception ex)
        {
            WorkspaceStatusText.Text = $"实时检测设置保存失败：{ex.Message}";
        }
    }

    private async Task PrepareModelsAsync()
    {
        foreach (var panel in _panels.Where(panel => panel.TaskId > 0))
        {
            try
            {
                await panel.PrepareModelAsync();
            }
            catch (Exception ex)
            {
                WorkspaceStatusText.Text = $"任务模型预加载失败：{ex.Message}";
            }
        }
    }

    private static void RestorePanelRoi(LiveTaskPanel panel)
    {
        var settings = AppServices.Instance.Settings;
        if (settings.LiveRois is not null && settings.LiveRois.TryGetValue(panel.TaskId, out var roi))
        {
            panel.RestoreRoi(hasOverride: true, roi: roi);
        }
        else
        {
            panel.RestoreRoi(hasOverride: false, roi: null);
        }
    }

    private void ApplyLayout()
    {
        if (PanelsHost is null || LayoutCombo is null)
        {
            return;
        }

        var layout = (LayoutCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "grid";
        var count = Math.Max(1, _panels.Count);
        var (rows, columns) = layout switch
        {
            "vertical" => (count, 1),
            "horizontal" => (1, count),
            _ => GetGridSize(count),
        };

        // ItemsControl 默认使用 StackPanel。这里始终配置 XAML 中的 UniformGrid，
        // 让“田字格 / 纵向 / 横向”只改变行列数，不再依赖运行时替换 ItemsPanel 模板。
        PanelsHost.ApplyTemplate();
        if (FindVisualChild<UniformGrid>(PanelsHost) is { } uniformGrid)
        {
            uniformGrid.Rows = rows;
            uniformGrid.Columns = columns;
        }

        foreach (var panel in _panels)
        {
            // 由 UniformGrid 负责测量和排列，清除之前版本留下的固定尺寸。
            panel.ClearValue(FrameworkElement.HeightProperty);
            panel.ClearValue(FrameworkElement.WidthProperty);
        }
    }

    private static (int Rows, int Columns) GetGridSize(int count)
    {
        var columns = count switch
        {
            <= 1 => 1,
            2 => 2,
            _ => (int)Math.Ceiling(Math.Sqrt(count)),
        };
        var rows = (int)Math.Ceiling(count / (double)columns);
        return (Math.Max(1, rows), Math.Max(1, columns));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void UpdateWorkspaceStatus()
    {
        if (_panels.Count == 0)
        {
            WorkspaceStatusText.Text = "请选择任务并点击“添加任务”。每个任务拥有独立的预览、结果和控制按钮。";
            return;
        }

        WorkspaceStatusText.Text = $"当前工作区已添加 {_panels.Count} 个任务；可以在每个任务面板中独立开始、暂停、单次检测或结束批次。";
    }

    private async Task ShutdownPanelsAsync()
    {
        foreach (var panel in _panels.ToArray())
        {
            try
            {
                await panel.ShutdownAsync();
            }
            catch
            {
                // 页面切换时尽力释放每个任务的资源，单个任务失败不影响其余任务。
            }
        }
    }

    private void OnRecordCompleted(object? sender, RecordCompletedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            VisibleText.Text = $"当前可见: {e.Output.GetCount()}";
            TotalText.Text = $"累计: {e.CountAfter}";
            var state = _run?.Counting.State;
            ForwardText.Text = $"正向: {state?.ForwardTotal ?? 0}";
            ReverseText.Text = $"反向: {state?.ReverseTotal ?? 0}";
            DrawCompatibilityOverlay(e.Output);
        });
    }

    private void DrawCompatibilityOverlay(AlgorithmOutput output)
    {
        OverlayCanvas.Children.Clear();
        if (PreviewImage.Source is null || OverlayCanvas.ActualWidth <= 0 || OverlayCanvas.ActualHeight <= 0)
        {
            return;
        }

        var srcW = PreviewImage.Source.Width;
        var srcH = PreviewImage.Source.Height;
        var scale = Math.Min(OverlayCanvas.ActualWidth / srcW, OverlayCanvas.ActualHeight / srcH);
        var drawW = srcW * scale;
        var drawH = srcH * scale;
        var offsetX = (OverlayCanvas.ActualWidth - drawW) / 2;
        var offsetY = (OverlayCanvas.ActualHeight - drawH) / 2;
        double Px(double x) => offsetX + x * drawW;
        double Py(double y) => offsetY + y * drawH;

        if (_activeRecipe?.CountingLine is { } line)
        {
            OverlayCanvas.Children.Add(new Line
            {
                X1 = Px(line.A.X),
                Y1 = Py(line.A.Y),
                X2 = Px(line.B.X),
                Y2 = Py(line.B.Y),
                Stroke = Brushes.Yellow,
                StrokeThickness = 2,
            });
            var dx = line.B.X - line.A.X;
            var dy = line.B.Y - line.A.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1e-6 && line.Hysteresis > 0)
            {
                var nx = dy / length;
                var ny = -dx / length;
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    OverlayCanvas.Children.Add(new Line
                    {
                        X1 = Px(line.A.X + sign * nx * line.Hysteresis),
                        Y1 = Py(line.A.Y + sign * ny * line.Hysteresis),
                        X2 = Px(line.B.X + sign * nx * line.Hysteresis),
                        Y2 = Py(line.B.Y + sign * ny * line.Hysteresis),
                        Stroke = Brushes.Goldenrod,
                        StrokeThickness = 1,
                        StrokeDashArray = [4, 3],
                    });
                }
            }
        }

        foreach (var track in output.Tracks)
        {
            var rect = new Rect(Px(track.Box.X), Py(track.Box.Y), track.Box.Width * drawW, track.Box.Height * drawH);
            var box = new Rectangle
            {
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Stroke = Brushes.Cyan,
                StrokeThickness = 1.4,
            };
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            OverlayCanvas.Children.Add(box);

            var label = new TextBlock { Text = $"#{track.TrackId}", Foreground = Brushes.Cyan, FontSize = 11 };
            Canvas.SetLeft(label, rect.X);
            Canvas.SetTop(label, Math.Max(0, rect.Y - 16));
            OverlayCanvas.Children.Add(label);

            if (track.Trail.Count > 1)
            {
                var trail = new Polyline { Stroke = Brushes.Orange, StrokeThickness = 1.4 };
                foreach (var point in track.Trail)
                {
                    trail.Points.Add(new Point(Px(point.X), Py(point.Y)));
                }
                OverlayCanvas.Children.Add(trail);
            }
        }
    }
}
