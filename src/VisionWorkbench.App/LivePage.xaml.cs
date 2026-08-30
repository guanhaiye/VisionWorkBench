using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

using VisionWorkbench.Application;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

public partial class LivePage : UserControl
{
    private readonly List<LiveTaskPanel> _panels = [];
    private LiveTaskItem[] _availableTasks = [];
    private bool _loadingTasks;
    // 兼容原有 UI 冒烟测试；正式运行时每个 LiveTaskPanel 使用自己的画布。
    private Recipe? _activeRecipe = null;
    private DetectionRunService? _run = null;

    public LivePage()
    {
        InitializeComponent();
        LayoutCombo.SelectedIndex = 0;
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
                .Select(t => new LiveTaskItem(t.Entity.Id, t.Recipe.StationCode, t.Recipe.Name))
                .ToArray();
            _availableTasks = items;
            foreach (var panel in _panels)
            {
                panel.SetAvailableTasks(_availableTasks);
            }

            if (_panels.Count == 0)
            {
                AddTaskPanel();
            }
            else
            {
                UpdateWorkspaceStatus();
            }
        }
        catch (Exception ex)
        {
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

    private void AddTaskPanel()
    {
        var panel = new LiveTaskPanel(_availableTasks);
        panel.RemoveRequested += Panel_RemoveRequested;
        panel.TaskSelectionChanged += Panel_TaskSelectionChanged;
        _panels.Add(panel);
        PanelsHost.Items.Add(panel);
        ApplyLayout();
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

        UpdateWorkspaceStatus();
    }

    private async void Panel_RemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not LiveTaskPanel panel)
        {
            return;
        }

        panel.RemoveRequested -= Panel_RemoveRequested;
        panel.TaskSelectionChanged -= Panel_TaskSelectionChanged;
        await panel.ShutdownAsync();
        _panels.Remove(panel);
        PanelsHost.Items.Remove(panel);
        UpdateWorkspaceStatus();
    }

    private async void RefreshTasks_Click(object sender, RoutedEventArgs e)
    {
        await LoadTasksAsync();
    }

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            ApplyLayout();
        }
    }

    private void ApplyLayout()
    {
        if (PanelsHost is null || LayoutCombo is null)
        {
            return;
        }

        FrameworkElementFactory factory;
        switch ((LayoutCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString())
        {
            case "vertical":
                factory = new FrameworkElementFactory(typeof(StackPanel));
                factory.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
                break;
            case "horizontal":
                factory = new FrameworkElementFactory(typeof(StackPanel));
                factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                break;
            default:
                factory = new FrameworkElementFactory(typeof(UniformGrid));
                factory.SetValue(UniformGrid.ColumnsProperty, 2);
                break;
        }

        PanelsHost.ItemsPanel = new ItemsPanelTemplate(factory);
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
