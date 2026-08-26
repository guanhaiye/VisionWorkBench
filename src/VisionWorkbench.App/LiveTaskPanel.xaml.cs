using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>实时检测页（文档 §8.2）：预览+叠加、结果面板、批次控制、人工修正。</summary>
public partial class LiveTaskPanel : UserControl
{
    private readonly PreviewRenderer _preview = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DetectionRunService? _run;
    private IAlgorithmSession? _algorithmSession;
    private ICameraSession? _cameraSession;
    private long _okCount;
    private long _ngCount;
    private long _reviewCount;
    private DateTimeOffset _algoFpsWindow = DateTimeOffset.UtcNow;
    private int _algoFpsFrames;
    private double _algoFps;
    private Recipe? _activeRecipe;
    private AlgorithmOutput? _lastOutput;
    private int _reconnectInProgress;
    private int _singleFrameNextIndex;
    private bool _singleFrameRun;
    private bool _singleFrameReceived;
    private bool _singleFrameBusy;
    private bool _overlayRedrawPending;
    private readonly CameraOpenOptions _cameraOptions = new() { FrameIntervalMs = 200, Loop = false };

    private IReadOnlyList<LiveTaskItem> _availableTasks = [];

    public event EventHandler? RemoveRequested;
    public event EventHandler? TaskSelectionChanged;

    public LiveTaskPanel(IReadOnlyList<LiveTaskItem> tasks)
    {
        InitializeComponent();
        SetAvailableTasks(tasks);
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    public long TaskId => (TaskCombo.SelectedItem as LiveTaskItem)?.Id ?? 0;

    public string SelectedTaskName => (TaskCombo.SelectedItem as LiveTaskItem)?.Name ?? "未选择任务";

    public void SetAvailableTasks(IReadOnlyList<LiveTaskItem> tasks)
    {
        var selectedId = TaskId;
        _availableTasks = tasks;
        TaskCombo.ItemsSource = _availableTasks;
        if (selectedId != 0)
        {
            TaskCombo.SelectedItem = _availableTasks.FirstOrDefault(t => t.Id == selectedId);
        }
        else
        {
            TaskCombo.SelectedIndex = -1;
        }
        TaskCombo.IsEnabled = _run is null;
    }

    public void ClearTaskSelection()
    {
        TaskCombo.SelectedIndex = -1;
    }

    public async Task ShutdownAsync()
    {
        _statusTimer.Stop();
        await CleanupAsync();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
        => RemoveRequested?.Invoke(this, EventArgs.Empty);

    private void TaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => TaskSelectionChanged?.Invoke(this, EventArgs.Empty);

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartRunAsync(singleFrame: false);

    private async Task StartRunAsync(bool singleFrame)
    {
        if (TaskCombo.SelectedItem is not LiveTaskItem item)
        {
            MessageBox.Show("请先在当前任务面板中选择任务。", "实时检测");
            return;
        }
        if (singleFrame && _singleFrameBusy)
        {
            return;
        }
        if (singleFrame)
        {
            _singleFrameBusy = true;
            SetButtons(running: true);
        }
        var found = await AppServices.Instance.Recipes.FindAsync(item.Id);
        if (found is not { } pair)
        {
            _singleFrameBusy = false;
            SetButtons(running: false);
            MessageBox.Show("任务数据无效", "错误");
            return;
        }
        var (entity, recipe) = pair;
        if (singleFrame && recipe.CameraProviderId is not ("image-folder" or "video-file"))
        {
            _singleFrameBusy = false;
            SetButtons(running: false);
            MessageBox.Show("当前输入源不是图片目录或视频文件。实时相机请先点击“开始”，再使用“单次检测”。", "单次检测", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _activeRecipe = recipe;
        if (!singleFrame)
        {
            _singleFrameNextIndex = 0;
        }
        _singleFrameRun = singleFrame;
        _singleFrameReceived = false;
        if (singleFrame)
        {
            OverlayCanvas.Children.Clear();
            StatusText.Text = "单次检测处理中，请稍候…";
        }
        var svcs = AppServices.Instance;
        var logger = svcs.LoggerFactory.CreateLogger<LiveTaskPanel>();

        try
        {
            // 1. 相机会话（虚拟源参数：间隔 200ms）
            _cameraSession = await OpenCameraAsync(recipe, CancellationToken.None, singleFrame);

            // 2. 算法会话
            _algorithmSession = await svcs.AlgorithmManager.CreateSessionAsync(
                recipe.PluginId, CancellationToken.None);

            // 3. 检测运行时 + 批次：优先恢复上次异常退出遗留的 running 批次
            _run = new DetectionRunService(svcs.Records, svcs.TempImages,
                svcs.LoggerFactory.CreateLogger<DetectionRunService>(), $"task-{entity.Id}", svcs.ResultPublisher);
            var batch = await svcs.BatchService.ResumeOrStartAsync(
                entity.Id, _run.Counting, svcs.Records);
            _run.PreviewReceived += (_, frame) => RenderPreview(frame);
            _run.RecordCompleted += OnRecordCompleted;
            _run.Faulted += OnRunFaulted;
            _run.SourceCompleted += async (_, _) => await Dispatcher.InvokeAsync(StopFromSourceEnd);
            await _run.StartAsync(recipe, entity.Id, _cameraSession, _algorithmSession, batch.Id);

            _okCount = _ngCount = _reviewCount = 0;
            VisibleText.Text = "当前可见: 0";
            TotalText.Text = "累计: 0";
            ForwardText.Text = "正向: 0";
            ReverseText.Text = "反向: 0";
            BatchText.Text = $"批次: {batch.BatchNumber}";
            StatusText.Text = singleFrame
                ? "单次检测已启动：图片/视频源只处理一帧。"
                : "批量检测已启动：图片/视频源将按顺序处理全部内容。";
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
        if (_singleFrameBusy)
        {
            return;
        }
        if (_run is not null && _run.State is (DetectionRunState.Running or DetectionRunState.Paused))
        {
            if (_activeRecipe?.CameraProviderId is ("image-folder" or "video-file"))
            {
                _singleFrameBusy = true;
                SetButtons(running: true);
                _singleFrameRun = true;
                _singleFrameReceived = false;
                try
                {
                    _cameraSession = await OpenCameraAsync(_activeRecipe, CancellationToken.None, true);
                    await _run.RestartCameraAsync(_cameraSession, CancellationToken.None);
                    StatusText.Text = "\u5355\u6b21\u68c0\u6d4b\u5904\u7406\u4e2d\uff08\u5df2\u590d\u7528\u6a21\u578b\uff09\u2026";
                }
                catch (Exception ex)
                {
                    _singleFrameBusy = false;
                    _singleFrameRun = false;
                    SetButtons(running: true);
                    StatusText.Text = $"\u5355\u6b21\u68c0\u6d4b\u542f\u52a8\u5931\u8d25\uff1a{ex.Message}";
                    if (_cameraSession is not null)
                    {
                        await _cameraSession.DisposeAsync();
                        _cameraSession = null;
                    }
                }
                return;
            }

            var result = await _run.SubmitSingleAsync(TimeSpan.FromSeconds(10));
            if (result is null)
                StatusText.Text = "单次检测：等待帧超时或源已播完";
            return;
        }
        await StartRunAsync(singleFrame: true);
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

    /// <summary>计数清零（CNT-S-010 / CNT-L-012）：原因必填；流模式同步清 worker 跟踪记忆。</summary>
    private async void ResetCount_Click(object sender, RoutedEventArgs e)
    {
        if (_run is null)
        {
            return;
        }
        var dialog = new CorrectionDialog("计数清零", hideDelta: true) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var adjustment = await _run.ResetCountAsync(
                dialog.Reason, AppServices.Instance.Settings.OperatorName);
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
        // 先等待已进入调度器的帧处理完成，避免单帧模式在源结束时丢失唯一图片。
        if (_run is not null)
        {
            try
            {
                await _run.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                StatusText.Text = "输入源已结束，但等待最后一帧处理超时。";
            }
        }
        // 有限源（图片目录/视频）播完：自动结束批次
        if (_singleFrameRun)
        {
            if (_singleFrameReceived)
            {
                _singleFrameNextIndex++;
                StatusText.Text = "\u5355\u6b21\u68c0\u6d4b\u5df2\u5b8c\u6210\uff0c\u53ef\u7ee7\u7eed\u70b9\u51fb\u5355\u6b21\u68c0\u6d4b\uff08\u6a21\u578b\u5df2\u590d\u7528\uff09";
            }
            else
            {
                StatusText.Text = "\u56fe\u7247\u6e90\u5df2\u5904\u7406\u5b8c\u6bd5";
            }
            _singleFrameRun = false;
            _singleFrameReceived = false;
            _singleFrameBusy = false;
            SetButtons(running: true);
            return;
        }

        if (_singleFrameRun && _singleFrameReceived)
        {
            _singleFrameNextIndex++;
        }
        _singleFrameRun = false;
        _singleFrameReceived = false;
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
        if (_algorithmSession is not null)
        {
            await _algorithmSession.DisposeAsync();
            _algorithmSession = null;
        }
        if (_cameraSession is not null)
        {
            await _cameraSession.DisposeAsync();
            _cameraSession = null;
        }
        _singleFrameBusy = false;
        _singleFrameRun = false;
        _singleFrameReceived = false;
        await Dispatcher.InvokeAsync(() => SetButtons(running: false));
    }

    private async void OnRunFaulted(object? sender, RunFaultedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = $"故障[{e.Source}] {e.Code}: {e.Message}";
            if (e.Source == "camera"
                && e.Code == CameraErrorCodes.DeviceDisconnected
                && _run is not null
                && _activeRecipe is not null
                && Interlocked.Exchange(ref _reconnectInProgress, 1) == 0)
            {
                _ = ReconnectCameraAfterFaultAsync();
                return;
            }
            SetButtons(running: false);
        });
    }

    private async Task ReconnectCameraAfterFaultAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = "相机断线，正在自动重连…");
            var replacement = await _run!.ReconnectCameraAsync(
                ct => OpenCameraAsync(_activeRecipe!, ct),
                maxAttempts: 5,
                retryDelay: TimeSpan.FromSeconds(1));
            if (replacement is not null)
            {
                _cameraSession = replacement;
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = "相机已重连，检测继续";
                    SetButtons(running: true);
                });
                return;
            }
        }
        catch (Exception ex)
        {
            AppServices.Instance.LoggerFactory.CreateLogger<LiveTaskPanel>()
                .LogError(ex, "相机自动重连失败");
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInProgress, 0);
        }

        await CleanupAsync();
    }

    private async Task<ICameraSession> OpenCameraAsync(
        Recipe recipe, CancellationToken cancellationToken, bool singleFrame = false)
    {
        var descriptor = new CameraDescriptor
        {
            ProviderId = recipe.CameraProviderId,
            DeviceId = recipe.CameraDeviceId,
            DisplayName = recipe.CameraDeviceId,
        };
        var options = singleFrame && recipe.CameraProviderId is ("image-folder" or "video-file")
            ? _cameraOptions with { MaxFrames = 1, StartFrameIndex = _singleFrameNextIndex }
            : _cameraOptions;
        var session = await AppServices.Instance.Cameras.OpenSessionAsync(
            descriptor, options, cancellationToken);
        await session.OpenAsync(options, cancellationToken);
        return session;
    }

    // ---- 渲染回调（DetectionRunService 从相机线程调用）----

    private void RenderPreview(VideoFrame frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _preview.Render(PreviewImage, frame);
        });
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastOutput is null || _overlayRedrawPending)
        {
            return;
        }

        // 窗口最大化、缩小或任务布局变化只改变控件尺寸，推理结果本身没有变化；
        // 在 Render 优先级重绘叠加层，使 Image 的 Uniform 留白和框坐标重新同步。
        _overlayRedrawPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _overlayRedrawPending = false;
            if (_lastOutput is not null)
            {
                DrawOverlay(_lastOutput);
            }
        }));
    }

    // ---- 检测完成（后台线程）→ UI 编组 ----

    private void OnRecordCompleted(object? sender, RecordCompletedEventArgs e)
    {
        if (_singleFrameRun)
        {
            _singleFrameReceived = true;
        }
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
            var behaviorResults = e.Output.Keypoints
                .Where(keypoint => !string.IsNullOrWhiteSpace(keypoint.BehaviorClass))
                .GroupBy(keypoint => keypoint.TrackId ?? "", StringComparer.Ordinal)
                .Select(group =>
                {
                    var item = group.Last();
                    return item.BehaviorProbability is { } probability
                        ? $"{item.BehaviorClass} {probability:0.00}"
                        : item.BehaviorClass!;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SummaryText.Text = behaviorResults.Length > 0
                ? $"工位 {e.StationCode} · 行为识别：{string.Join("、", behaviorResults)}"
                : $"工位 {e.StationCode} · {e.Decision.Summary}";
            CountText.Text = $"数量: {e.Output.GetCount()}";
            ElapsedText.Text = $"算法耗时: {e.Output.Performance?.TotalMs ?? 0:0.#} ms";
            DetectionListText.Text = behaviorResults.Length > 0
                ? string.Join("\n", behaviorResults.Select(item => $"行为 {item}"))
                : string.Join("\n", e.Output.Detections.Take(8)
                    .Select(d => $"{d.ClassName} {d.Confidence:0.00}"));
            TotalText.Text = $"累计: {e.CountAfter}";
            VisibleText.Text = $"当前可见: {e.Output.GetCount()}";
            var state = _run?.Counting.State;
            ForwardText.Text = $"正向: {state?.ForwardTotal ?? 0}";
            ReverseText.Text = $"反向: {state?.ReverseTotal ?? 0}";
            switch (e.Decision.Status)
            {
                case DecisionStatus.Ok: _okCount++; break;
                case DecisionStatus.Ng: _ngCount++; break;
                case DecisionStatus.ReviewRequired: _reviewCount++; break;
            }
            RecordStatsText.Text = $"OK {_okCount} / NG {_ngCount} / 待确认 {_reviewCount}";

            // 结果必须绘制在本次推理对应的原始帧上，避免批量处理时错叠到下一张图片。
            if (e.Frame is not null)
            {
                _preview.Render(PreviewImage, e.Frame, force: true);
            }
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

        // YOLO11 instance/semantic segmentation overlays.
        foreach (var segmentation in output.Segmentations)
        {
            foreach (var contour in segmentation.Contours)
            {
                if (contour.Count < 3)
                {
                    continue;
                }
                var semantic = segmentation.Mode.Equals("semantic", StringComparison.OrdinalIgnoreCase);
                var polygon = new Polygon
                {
                    Fill = semantic
                        ? new SolidColorBrush(Color.FromArgb(48, 255, 165, 0))
                        : new SolidColorBrush(Color.FromArgb(56, 0, 191, 255)),
                    Stroke = semantic ? Brushes.Orange : Brushes.DeepSkyBlue,
                    StrokeThickness = 1.2,
                };
                foreach (var point in contour)
                {
                    polygon.Points.Add(new System.Windows.Point(
                        offsetX + point.X * drawW,
                        offsetY + point.Y * drawH));
                }
                OverlayCanvas.Children.Add(polygon);
            }
        }

        // 流水模式有轨迹输出：Track 框/ID/Trail 取代裸检测框（含丢失中的轨迹）
        if (output.Tracks.Count > 0)
        {
            foreach (var track in output.Tracks)
            {
                var rect = new System.Windows.Rect(
                    offsetX + track.Box.X * drawW,
                    offsetY + track.Box.Y * drawH,
                    track.Box.Width * drawW,
                    track.Box.Height * drawH);
                var box = new Rectangle
                {
                    Width = Math.Max(1, rect.Width),
                    Height = Math.Max(1, rect.Height),
                    Stroke = track.TimeSinceUpdate > 0 ? Brushes.DarkCyan : Brushes.Cyan,
                    StrokeThickness = 1.4,
                };
                Canvas.SetLeft(box, rect.X);
                Canvas.SetTop(box, rect.Y);
                OverlayCanvas.Children.Add(box);

                var label = new TextBlock
                {
                    Text = output.Keypoints.FirstOrDefault(keypoint =>
                        string.Equals(keypoint.TrackId, track.TrackId, StringComparison.Ordinal)) is { } pose
                        && !string.IsNullOrWhiteSpace(pose.BehaviorClass)
                            ? $"{pose.BehaviorClass} {(pose.BehaviorProbability ?? 0):0.00}"
                            : $"#{track.TrackId}",
                    Foreground = output.Keypoints.FirstOrDefault(keypoint =>
                        string.Equals(keypoint.TrackId, track.TrackId, StringComparison.Ordinal))?.BehaviorClass is not null
                        ? Brushes.Orange
                        : Brushes.Cyan,
                    FontSize = 11,
                };
                Canvas.SetLeft(label, rect.X);
                Canvas.SetTop(label, Math.Max(0, rect.Y - 16));
                OverlayCanvas.Children.Add(label);

                if (track.Trail.Count > 1)
                {
                    var trail = new Polyline { Stroke = Brushes.Orange, StrokeThickness = 1.4 };
                    foreach (var p in track.Trail)
                    {
                        trail.Points.Add(new System.Windows.Point(
                            offsetX + p.X * drawW, offsetY + p.Y * drawH));
                    }
                    OverlayCanvas.Children.Add(trail);
                }
            }
        }
        else
        {
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

        // 检测线（实线）+ 滞回带边界（虚线）：LineCrossing 模式；未配置时按插件默认竖直中线
        CountingLineConfig? line = _activeRecipe?.CountingLine;
        if (_activeRecipe?.CountingMode == CountingMode.LineCrossing)
        {
            line ??= new CountingLineConfig
            {
                A = new NormalizedPoint { X = 0.5, Y = 0.1 },
                B = new NormalizedPoint { X = 0.5, Y = 0.9 },
            };
        }
        if (line is { } cfg)
        {
            double Px(double nx) => offsetX + nx * drawW;
            double Py(double ny) => offsetY + ny * drawH;
            var main = new Line
            {
                X1 = Px(cfg.A.X), Y1 = Py(cfg.A.Y),
                X2 = Px(cfg.B.X), Y2 = Py(cfg.B.Y),
                Stroke = Brushes.Yellow, StrokeThickness = 2,
            };
            OverlayCanvas.Children.Add(main);
            // 带边界 = 法向偏移 hysteresis（归一化空间法向，按轴缩放到像素）
            var dx = cfg.B.X - cfg.A.X;
            var dy = cfg.B.Y - cfg.A.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-6 && cfg.Hysteresis > 0)
            {
                var nx = dy / len;
                var ny = -dx / len;
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    var edge = new Line
                    {
                        X1 = Px(cfg.A.X + sign * nx * cfg.Hysteresis),
                        Y1 = Py(cfg.A.Y + sign * ny * cfg.Hysteresis),
                        X2 = Px(cfg.B.X + sign * nx * cfg.Hysteresis),
                        Y2 = Py(cfg.B.Y + sign * ny * cfg.Hysteresis),
                        Stroke = Brushes.Goldenrod,
                        StrokeThickness = 1,
                        StrokeDashArray = [4, 3],
                        Opacity = 0.8,
                    };
                    OverlayCanvas.Children.Add(edge);
                }
            }
        }
    }

    private void UpdateStatus()
    {
        var svcs = AppServices.Instance;
        var provider = _activeRecipe?.ExecutionProvider ?? svcs.Settings.ExecutionProvider;
        StatusText.Text =
            $"相机 FPS: {_preview.Fps:0.#} | 算法 FPS: {_algoFps:0.#} | 后端: {provider}"
            + $" | 数据库: {System.IO.Path.Combine(svcs.Settings.DataDirectory, "visionworkbench.db")}"
            + (_run is null ? "" : $" | 丢帧: {_run.Scheduler.DroppedFrameCount}");
    }

    private void SetButtons(bool running)
    {
        StartButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        PauseButton.Content = "暂停";
        SingleButton.IsEnabled = !_singleFrameBusy;
        StopButton.IsEnabled = running;
        AdjustButton.IsEnabled = running;
        ResetCountButton.IsEnabled = running;
        TaskCombo.IsEnabled = !running;
    }

}
