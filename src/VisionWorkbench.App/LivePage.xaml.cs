using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>实时检测页（文档 §8.2）：预览+叠加、结果面板、批次控制、人工修正。</summary>
public partial class LivePage : UserControl
{
    private readonly PreviewRenderer _preview = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DetectionRunService? _run;
    private ICameraSession? _cameraSession;
    private long _okCount;
    private long _ngCount;
    private long _reviewCount;
    private DateTimeOffset _algoFpsWindow = DateTimeOffset.UtcNow;
    private int _algoFpsFrames;
    private double _algoFps;
    private Recipe? _activeRecipe;
    private AlgorithmOutput? _lastOutput;

    public LivePage()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Loaded += (_, _) => LoadTasks();
    }

    public void OnShown() => LoadTasks();

    private async void LoadTasks()
    {
        try
        {
            var tasks = await AppServices.Instance.Recipes.ListAsync();
            TaskCombo.ItemsSource = tasks.Select(t => new TaskItem(t.Entity.Id, t.Recipe.Name)).ToArray();
            if (TaskCombo.Items.Count > 0)
            {
                TaskCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (TaskCombo.SelectedItem is not TaskItem item)
        {
            MessageBox.Show("请先在任务配置页创建任务", "提示");
            return;
        }
        var found = await AppServices.Instance.Recipes.FindAsync(item.Id);
        if (found is not { } pair)
        {
            MessageBox.Show("任务数据无效", "错误");
            return;
        }
        var (entity, recipe) = pair;
        _activeRecipe = recipe;
        var svcs = AppServices.Instance;
        var logger = svcs.LoggerFactory.CreateLogger<LivePage>();

        try
        {
            // 1. 相机会话（虚拟源参数：间隔 200ms）
            var descriptor = new CameraDescriptor
            {
                ProviderId = recipe.CameraProviderId,
                DeviceId = recipe.CameraDeviceId,
                DisplayName = recipe.CameraDeviceId,
            };
            _cameraSession = await svcs.Cameras.OpenSessionAsync(
                descriptor, new CameraOpenOptions { FrameIntervalMs = 200, Loop = false }, CancellationToken.None);
            await _cameraSession.OpenAsync(new CameraOpenOptions { FrameIntervalMs = 200 }, CancellationToken.None);

            // 2. 算法会话
            var algorithm = await svcs.AlgorithmManager.CreateSessionAsync(recipe.PluginId, CancellationToken.None);

            // 3. 批次 + 检测运行时
            var batch = await svcs.BatchService.StartAsync(entity.Id, 0);
            _run = new DetectionRunService(svcs.Records, svcs.TempImages,
                svcs.LoggerFactory.CreateLogger<DetectionRunService>(), $"task-{entity.Id}");
            _run.PreviewReceived += (_, frame) => RenderPreview(frame);
            _run.RecordCompleted += OnRecordCompleted;
            _run.Faulted += OnRunFaulted;
            _run.SourceCompleted += async (_, _) => await Dispatcher.InvokeAsync(StopFromSourceEnd);
            await _run.StartAsync(recipe, entity.Id, _cameraSession, algorithm, batch.Id);

            _okCount = _ngCount = _reviewCount = 0;
            BatchText.Text = $"批次: {batch.BatchNumber}";
            SetButtons(running: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "开始检测失败");
            MessageBox.Show($"开始检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            await CleanupAsync();
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_run is null)
        {
            return;
        }
        if (_run.State == DetectionRunState.Running)
        {
            await _run.PauseAsync();
            PauseButton.Content = "继续";
        }
        else if (_run.State == DetectionRunState.Paused)
        {
            await _run.ResumeAsync();
            PauseButton.Content = "暂停";
        }
    }

    private async void Single_Click(object sender, RoutedEventArgs e)
    {
        if (_run is null)
        {
            return;
        }
        var result = await _run.SubmitSingleAsync(TimeSpan.FromSeconds(10));
        if (result is null)
        {
            StatusText.Text = "单次检测：等待帧超时或源已播完";
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopBatchAsync();

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        if (_run is null)
        {
            return;
        }
        var dialog = new CorrectionDialog("人工修正计数") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var adjustment = await _run.AdjustCountAsync(
                dialog.Delta, dialog.Reason, AppServices.Instance.Settings.OperatorName);
            TotalText.Text = $"累计: {adjustment.After}";
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "校验失败");
        }
    }

    private async Task StopBatchAsync()
    {
        if (_run is null)
        {
            return;
        }
        var total = _run.Counting.State.CurrentTotal;
        await _run.StopAsync();
        await AppServices.Instance.BatchService.EndAsync(total);
        await CleanupAsync();
        BatchText.Text = "批次: 已结束";
    }

    private async Task StopFromSourceEnd()
    {
        // 有限源（图片目录/视频）播完：自动结束批次
        await StopBatchAsync();
    }

    private async Task CleanupAsync()
    {
        if (_run is not null)
        {
            _run.RecordCompleted -= OnRecordCompleted;
            _run.Faulted -= OnRunFaulted;
            await _run.DisposeAsync();
            _run = null;
        }
        if (_cameraSession is not null)
        {
            await _cameraSession.DisposeAsync();
            _cameraSession = null;
        }
        SetButtons(running: false);
    }

    private async void OnRunFaulted(object? sender, RunFaultedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = $"故障[{e.Source}] {e.Code}: {e.Message}";
            SetButtons(running: false);
        });
    }

    // ---- 渲染回调（DetectionRunService 从相机线程调用）----

    private void RenderPreview(VideoFrame frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _preview.Render(PreviewImage, frame);
        });
    }

    // ---- 检测完成（后台线程）→ UI 编组 ----

    private void OnRecordCompleted(object? sender, RecordCompletedEventArgs e)
    {
        _algoFpsFrames++;
        var now = DateTimeOffset.UtcNow;
        if (now - _algoFpsWindow >= TimeSpan.FromSeconds(1))
        {
            _algoFps = _algoFpsFrames / (now - _algoFpsWindow).TotalSeconds;
            _algoFpsWindow = now;
            _algoFpsFrames = 0;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _lastOutput = e.Output;
            var (text, color) = e.Decision.Status switch
            {
                DecisionStatus.Ok => ("OK", Brushes.Green),
                DecisionStatus.Ng => ("NG", Brushes.Red),
                DecisionStatus.ReviewRequired => ("待确认", Brushes.Orange),
                _ => ("错误", Brushes.Gray),
            };
            DecisionText.Text = text;
            DecisionText.Foreground = color;
            SummaryText.Text = e.Decision.Summary;
            CountText.Text = $"数量: {e.Output.GetCount()}";
            ElapsedText.Text = $"算法耗时: {e.Output.Performance?.TotalMs ?? 0:0.#} ms";
            DetectionListText.Text = string.Join("\n", e.Output.Detections.Take(8)
                .Select(d => $"{d.ClassName} {d.Confidence:0.00}"));
            TotalText.Text = $"累计: {e.CountAfter}";
            switch (e.Decision.Status)
            {
                case DecisionStatus.Ok: _okCount++; break;
                case DecisionStatus.Ng: _ngCount++; break;
                case DecisionStatus.ReviewRequired: _reviewCount++; break;
            }
            RecordStatsText.Text = $"OK {_okCount} / NG {_ngCount} / 待确认 {_reviewCount}";

            DrawOverlay(e.Output);
            if (e.Decision.Status is DecisionStatus.Ng or DecisionStatus.ReviewRequired
                && e.Record.AnnotatedImagePath is { } path && File.Exists(path))
            {
                try
                {
                    EvidenceImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(path));
                }
                catch (IOException)
                {
                    // 图片被清理时忽略
                }
            }
        });
    }

    /// <summary>归一化检测框 → 像素叠加（按预览控件实际尺寸）。</summary>
    private void DrawOverlay(AlgorithmOutput output)
    {
        OverlayCanvas.Children.Clear();
        if (PreviewImage.Source is null)
        {
            return;
        }
        // Stretch=Uniform 的实际显示区域
        var canvasW = OverlayCanvas.ActualWidth;
        var canvasH = OverlayCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0)
        {
            return;
        }
        var srcW = PreviewImage.Source.Width;
        var srcH = PreviewImage.Source.Height;
        var scale = Math.Min(canvasW / srcW, canvasH / srcH);
        var drawW = srcW * scale;
        var drawH = srcH * scale;
        var offsetX = (canvasW - drawW) / 2;
        var offsetY = (canvasH - drawH) / 2;

        foreach (var detection in output.Detections)
        {
            var rect = new System.Windows.Rect(
                offsetX + detection.Box.X * drawW,
                offsetY + detection.Box.Y * drawH,
                detection.Box.Width * drawW,
                detection.Box.Height * drawH);
            var box = new Rectangle
            {
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Stroke = Brushes.Lime,
                StrokeThickness = 1.6,
            };
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            OverlayCanvas.Children.Add(box);

            var label = new TextBlock
            {
                Text = $"{detection.ClassName} {detection.Confidence:0.00}",
                Foreground = Brushes.Lime,
                FontSize = 11,
            };
            Canvas.SetLeft(label, rect.X);
            Canvas.SetTop(label, Math.Max(0, rect.Y - 16));
            OverlayCanvas.Children.Add(label);
        }

        // ROI 框（配置了 ROI 时显示）
        if (_activeRecipe?.Roi is { } roi)
        {
            var roiRect = new System.Windows.Rect(
                offsetX + roi.X * drawW, offsetY + roi.Y * drawH,
                roi.Width * drawW, roi.Height * drawH);
            var box = new Rectangle
            {
                Width = Math.Max(1, roiRect.Width),
                Height = Math.Max(1, roiRect.Height),
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 1.2,
                StrokeDashArray = [4, 2],
            };
            Canvas.SetLeft(box, roiRect.X);
            Canvas.SetTop(box, roiRect.Y);
            OverlayCanvas.Children.Add(box);
        }
    }

    private void UpdateStatus()
    {
        var svcs = AppServices.Instance;
        StatusText.Text =
            $"相机 FPS: {_preview.Fps:0.#} | 算法 FPS: {_algoFps:0.#} | 后端: {svcs.Settings.ExecutionProvider}"
            + $" | 数据库: {System.IO.Path.Combine(svcs.Settings.DataDirectory, "visionworkbench.db")}"
            + (_run is null ? "" : $" | 丢帧: {_run.Scheduler.DroppedFrameCount}");
    }

    private void SetButtons(bool running)
    {
        StartButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        PauseButton.Content = "暂停";
        SingleButton.IsEnabled = running;
        StopButton.IsEnabled = running;
        AdjustButton.IsEnabled = running;
        TaskCombo.IsEnabled = !running;
    }

    private sealed record TaskItem(long Id, string Name)
    {
        public long Id { get; } = Id;
        public string Name { get; } = Name;
    }
}
