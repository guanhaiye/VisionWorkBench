using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

/// <summary>实时检测页（文档 §8.2）：预览+叠加、结果面板和批次控制。</summary>
public partial class LiveTaskPanel : UserControl
{
    private void WorkspaceContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 右侧指标、质量卡片和底部操作区保持可见；仅步骤列表在剩余高度内滚动。
        SopStepsScrollViewer.MaxHeight = Math.Clamp(e.NewSize.Height - 380, 96, 420);
    }

    private void WorkspaceContentGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // The shared ImageViewer owns wheel zoom while the pointer is over the
        // preview. Let that control preserve the image point beneath the mouse.
        if (PreviewViewer.IsMouseOver)
        {
            return;
        }

        var direction = -Math.Sign(e.Delta);
        const double wheelStep = 54;
        if (SopStepsScrollViewer.IsMouseOver && SopStepsScrollViewer.ScrollableHeight > 0)
        {
            var target = Math.Clamp(
                SopStepsScrollViewer.VerticalOffset + direction * wheelStep,
                0,
                SopStepsScrollViewer.ScrollableHeight);
            if (Math.Abs(target - SopStepsScrollViewer.VerticalOffset) > 0.1)
            {
                SopStepsScrollViewer.ScrollToVerticalOffset(target);
                e.Handled = true;
                return;
            }
        }
        if (DetailsScrollViewer.ScrollableHeight > 0)
        {
            var target = Math.Clamp(
                DetailsScrollViewer.VerticalOffset + direction * wheelStep,
                0,
                DetailsScrollViewer.ScrollableHeight);
            if (Math.Abs(target - DetailsScrollViewer.VerticalOffset) > 0.1)
            {
                DetailsScrollViewer.ScrollToVerticalOffset(target);
                e.Handled = true;
                return;
            }
        }

        var current = VisualTreeHelper.GetParent(this);
        while (current is not null && current is not ScrollViewer)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ScrollViewer outer && outer.ScrollableHeight > 0)
        {
            outer.ScrollToVerticalOffset(Math.Clamp(
                outer.VerticalOffset + direction * wheelStep,
                0,
                outer.ScrollableHeight));
            e.Handled = true;
        }
    }

    private sealed record SopStepView(string Indicator, string Name, string StatusText, Brush Brush);

    private readonly PreviewRenderer _preview = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DetectionRunService? _run;
    private IAlgorithmSession? _algorithmSession;
    private readonly Dictionary<string, IAlgorithmSession> _sopAlgorithmSessions = new(StringComparer.Ordinal);
    private ICameraSession? _cameraSession;
    private long _okCount;
    private long _ngCount;
    private readonly Queue<string> _detectionLog = new();
    private DateTimeOffset _algoFpsWindow = DateTimeOffset.UtcNow;
    private int _algoFpsFrames;
    private double _algoFps;
    private Recipe? _activeRecipe;
    private AlgorithmOutput? _lastOutput;
    private int _reconnectInProgress;
    private int _singleFrameNextIndex;
    private int _offlineImageTotal;
    private int _offlineImageStartIndex;
    private bool _singleFrameBusy;
    private bool _startInProgress;
    private bool _overlayRedrawPending;
    private bool _fitPreviewOnNextFrame = true;
    private bool _previewFitPending;
    // 结果返回前允许显示预览；首个结果返回后，只显示已经完成推理的帧，
    // 避免下一帧预览覆盖上一帧的检测结果。
    private long _lastRenderedResultSequence = -1;
    private long _lastRenderedPreviewSequence = -1;
    private bool _roiDrawing;
    private SopSnapshot? _lastSopSnapshot;
    private string? _lastSopRoundText;
    private ImageSource? _lastSopRoundImage;
    private bool _roiOverrideSet;
    private Point _roiDragStart;
    private NormalizedRect? _roiOverride;
    private NormalizedRect? _draftRoi;
    private long _preparedTaskId;
    private readonly SemaphoreSlim _modelPrepareGate = new(1, 1);
    private readonly CameraOpenOptions _cameraOptions = new() { FrameIntervalMs = 200, Loop = false };

    private IReadOnlyList<LiveTaskItem> _availableTasks = [];
    private bool _sopCycleActionBusy;

    public event EventHandler? RemoveRequested;
    public event EventHandler? TaskSelectionChanged;
    public event EventHandler? SettingsChanged;

    public LiveTaskPanel(IReadOnlyList<LiveTaskItem> tasks)
    {
        InitializeComponent();
        // ContextMenu 位于独立的 Popup 视觉树中，显式保存所属面板，
        // 避免右键菜单操作时误关联到其他实时检测面板。
        RemoveContextMenuItem.Tag = this;
        SetAvailableTasks(tasks);
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    public long TaskId => (TaskCombo.SelectedItem as LiveTaskItem)?.Id ?? 0;

    public string SelectedTaskName => (TaskCombo.SelectedItem as LiveTaskItem)?.Name ?? "未选择任务";

    public bool HasRoiOverride => _roiOverrideSet;

    public NormalizedRect? RoiOverride => _roiOverride;

    public void SetPanelNumber(int number)
    {
        TaskLabel.Text = $"任务 {Math.Max(1, number)}：";
    }

    public void SetRuntimeMode(bool enabled)
    {
        ControlsPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SelectTask(long taskId)
    {
        TaskCombo.SelectedItem = _availableTasks.FirstOrDefault(task => task.Id == taskId);
    }

    public void RestoreRoi(bool hasOverride, NormalizedRect? roi)
    {
        _roiDrawing = false;
        _draftRoi = null;
        _roiOverrideSet = hasOverride;
        _roiOverride = roi;
        RenderRoi();
    }

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
        await CleanupAsync(disposeAlgorithm: true);
    }

    /// <summary>启动实时检测面板时预先启动 Worker 并加载模型。</summary>
    public async Task PrepareModelAsync()
    {
        if (TaskCombo.SelectedItem is not LiveTaskItem item || _run is not null)
        {
            return;
        }

        await _modelPrepareGate.WaitAsync();
        try
        {
            if (_algorithmSession is { State: AlgorithmSessionState.Ready }
                && _preparedTaskId == item.Id)
            {
                return;
            }

            await DisposePreparedModelsAsync();

            var found = await AppServices.Instance.Recipes.FindAsync(item.Id);
            if (found is not { } pair)
            {
                throw new InvalidOperationException("任务数据无效");
            }

            var recipe = pair.Recipe with { Roi = _roiOverrideSet ? _roiOverride : pair.Recipe.Roi };
            StatusText.Text = "正在预加载模型…";
            var modelDefinitions = recipe.Sop?.Definition?.Steps
                .OrderBy(step => step.Order)
                .Where(step => !string.IsNullOrWhiteSpace(step.Execution?.PluginId))
                .Select(step => (step.Id, Execution: step.Execution!))
                .ToArray() ?? [];
            if (modelDefinitions.Length == 0)
            {
                modelDefinitions = [("__root", new SopStepExecution
                {
                    PluginId = recipe.PluginId,
                    TaskType = recipe.TaskType,
                    ExecutionProvider = recipe.ExecutionProvider,
                    SettingsJson = recipe.SettingsJson,
                    Roi = recipe.Roi,
                    RoiPolicy = recipe.RoiPolicy,
                    Rules = recipe.Rules,
                })];
            }

            var created = new List<IAlgorithmSession>();
            try
            {
                foreach (var model in modelDefinitions)
                {
                    var session = await AppServices.Instance.AlgorithmManager.CreateSessionAsync(
                        model.Execution.PluginId, CancellationToken.None);
                    created.Add(session);
                    var modelRecipe = recipe with
                    {
                        PluginId = model.Execution.PluginId,
                        TaskType = model.Execution.TaskType,
                        ExecutionProvider = model.Execution.ExecutionProvider,
                        SettingsJson = model.Execution.SettingsJson,
                        Roi = model.Execution.Roi,
                        RoiPolicy = model.Execution.RoiPolicy,
                        Rules = model.Execution.Rules,
                    };
                    await session.InitializeAsync(new AlgorithmInitialization
                    {
                        Settings = DetectionRunService.BuildAlgorithmSettings(modelRecipe),
                        ExecutionProvider = model.Execution.ExecutionProvider,
                    }, CancellationToken.None);
                    _sopAlgorithmSessions[model.Id] = session;
                }
            }
            catch
            {
                foreach (var session in created)
                {
                    await session.DisposeAsync();
                }
                _sopAlgorithmSessions.Clear();
                throw;
            }

            _algorithmSession = _sopAlgorithmSessions.Values.First();
            _preparedTaskId = item.Id;
            StatusText.Text = modelDefinitions.Length > 1
                ? $"已加载 {modelDefinitions.Length} 个 SOP 模型，等待开始检测"
                : "模型已加载，等待开始检测";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"模型预加载失败：{ex.Message}";
            throw;
        }
        finally
        {
            _modelPrepareGate.Release();
        }
    }

    private async void PrepareModelInBackground()
    {
        try
        {
            await PrepareModelAsync();
        }
        catch
        {
            // 预加载失败时保留页面状态，用户点击开始仍可看到明确错误并重试。
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
        => RequestRemove();

    private void RemoveFromContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: LiveTaskPanel panel } && ReferenceEquals(panel, this))
        {
            panel.RequestRemove();
        }
    }

    private void RequestRemove()
        => RemoveRequested?.Invoke(this, EventArgs.Empty);

    private void TaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _roiDrawing = false;
        _roiOverrideSet = false;
        _roiOverride = null;
        _draftRoi = null;
        RenderRoi();
        SetSopLayout(false);
        ResetStatistics();
        ClearDetectionLog();
        _ = RefreshTaskPresentationAsync();
        TaskSelectionChanged?.Invoke(this, EventArgs.Empty);
        PrepareModelInBackground();
    }

    private async Task RefreshTaskPresentationAsync()
    {
        var taskId = TaskId;
        if (taskId == 0)
        {
            TaskModeText.Text = "请选择任务";
            SetSopLayout(false);
            UpdateSopControls(null);
            return;
        }

        var found = await AppServices.Instance.Recipes.FindAsync(taskId);
        if (found is not { } pair || TaskId != taskId)
        {
            return;
        }

        _activeRecipe = pair.Recipe;
        TaskModeText.Text = GetTaskModeText(pair.Recipe);
        if (pair.Recipe.Sop?.Definition is { } definition)
        {
            SetSopLayout(true);
            UpdateSopUi(new SopStateMachine(definition, pair.Recipe.Sop.RunMode).Snapshot);
        }
        else
        {
            SetSopLayout(false);
            SopStepsItems.ItemsSource = null;
            UpdateSopControls(null);
        }
    }

    private static string GetTaskModeText(Recipe recipe)
    {
        if (recipe.Sop is not null)
        {
            return "SOP 工序检测";
        }
        return recipe.TaskType.Normalize() switch
        {
            InspectionTaskType.BehaviorRecognition => "人员行为识别",
            InspectionTaskType.SemanticSegmentation => "语义分割",
            InspectionTaskType.InstanceSegmentation => "实例分割",
            InspectionTaskType.AiText => "AI 文字识别",
            InspectionTaskType.Barcode => "条码识别",
            InspectionTaskType.QrCode => "二维码识别",
            _ when recipe.CountingMode != CountingMode.Snapshot => "视觉计数",
            _ => "普通视觉检测",
        };
    }

    private void UpdateSopUi(SopSnapshot snapshot)
    {
        _lastSopSnapshot = snapshot;
        UpdateSopMetrics(snapshot);
        SopCurrentStepText.Text = snapshot.Status switch
        {
            SopRunStatus.CompletedOk => "当前产品：SOP 完成",
            SopRunStatus.NgTimeout or SopRunStatus.NgConditionFailed or SopRunStatus.NgWrongOrder
                => $"当前产品：NG · {snapshot.FailureReason}",
            SopRunStatus.ReviewRequired => $"当前产品：待人工确认 · {snapshot.FailureReason}",
            SopRunStatus.Interrupted or SopRunStatus.Aborted => $"当前产品：已中止 · {snapshot.FailureReason}",
            _ => string.IsNullOrWhiteSpace(snapshot.CurrentStepName)
                ? "等待开始"
                : $"当前步骤：{snapshot.CurrentStepOrder:00} {snapshot.CurrentStepName}",
        };
        var quality = GetSopQuality(snapshot);
        SopCurrentQualityText.Text = quality.Text;
        SopCurrentQualityBadgeText.Text = quality.Badge;
        SopCurrentQualityBadge.Background = quality.Brush;
        SopStepsItems.ItemsSource = snapshot.Steps.Select(step =>
        {
            var (text, brush) = step.Status switch
            {
                SopStepStatus.Completed => ("已完成", Brushes.Green),
                SopStepStatus.InProgress => ("进行中", Brushes.DodgerBlue),
                SopStepStatus.Failed => ("失败", Brushes.Red),
                SopStepStatus.ReviewRequired => ("待确认", Brushes.DarkOrange),
                SopStepStatus.Skipped => ("已跳过", Brushes.Gray),
                _ => ("未完成", Brushes.Gray),
            };
            return new SopStepView($"{step.Order:00}", step.Name, text, brush);
        }).ToArray();
        UpdateSopControls(snapshot);
    }

    private void SetSopLayout(bool hasSop)
    {
        SopCard.Visibility = hasSop ? Visibility.Visible : Visibility.Collapsed;
        DecisionCard.Visibility = hasSop ? Visibility.Collapsed : Visibility.Visible;
        ResultCard.Visibility = hasSop ? Visibility.Collapsed : Visibility.Visible;
        StatsCard.Visibility = hasSop ? Visibility.Collapsed : Visibility.Visible;
        LogCard.Visibility = hasSop ? Visibility.Collapsed : Visibility.Visible;
        if (!hasSop)
        {
            _lastSopSnapshot = null;
            _lastSopRoundText = null;
            _lastSopRoundImage = null;
            SopPreviousImage.Source = null;
            SopPreviousImage.Visibility = Visibility.Collapsed;
            SopPreviousResultText.Text = "暂无上一轮结果";
        }
    }

    private void UpdateSopMetrics(SopSnapshot snapshot)
    {
        SopMetricCurrentStep.Text = string.IsNullOrWhiteSpace(snapshot.CurrentStepName)
            ? "--"
            : $"{snapshot.CurrentStepOrder:00} · {snapshot.CurrentStepName}";
        SopMetricCompleted.Text = $"{snapshot.CompletedCount} / {snapshot.TotalCount}";
        var current = snapshot.Steps.FirstOrDefault(step => step.Status == SopStepStatus.InProgress);
        SopMetricElapsed.Text = current?.StartedAt is { } started
            ? FormatElapsed(DateTimeOffset.UtcNow - started)
            : "--";
        SopMetricExceptions.Text = snapshot.Status is SopRunStatus.NgTimeout
            or SopRunStatus.NgConditionFailed or SopRunStatus.NgWrongOrder or SopRunStatus.ReviewRequired
            ? "1"
            : "0";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");
    }

    private static (string Text, string Badge, Brush Brush) GetSopQuality(SopSnapshot snapshot) =>
        snapshot.Status switch
        {
            SopRunStatus.CompletedOk => ("本轮外观检测合格", "OK", Brushes.SeaGreen),
            SopRunStatus.NgTimeout or SopRunStatus.NgConditionFailed or SopRunStatus.NgWrongOrder
                => ("本轮外观检测不合格", "NG", Brushes.IndianRed),
            SopRunStatus.ReviewRequired => ("本轮检测等待人工确认", "待确认", Brushes.DarkOrange),
            SopRunStatus.Interrupted or SopRunStatus.Aborted => ("本轮检测已中止", "中止", Brushes.Gray),
            _ when !string.IsNullOrWhiteSpace(snapshot.CurrentStepName)
                => ($"正在检测：{snapshot.CurrentStepName}", "进行中", Brushes.DodgerBlue),
            _ => ("等待步骤确认", "等待", Brushes.DarkOrange),
        };

    private void ShowPreviousSopResult()
    {
        SopPreviousResultText.Text = _lastSopRoundText ?? "暂无上一轮结果";
        SopPreviousImage.Source = _lastSopRoundImage;
        SopPreviousImage.Visibility = _lastSopRoundImage is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string BuildSopRoundText(RecordCompletedEventArgs args)
    {
        var completedStep = args.Sop?.Steps
            .Where(step => step.Status == SopStepStatus.Completed)
            .OrderByDescending(step => step.CompletedAt)
            .FirstOrDefault();
        var stepName = completedStep?.Name ?? args.Sop?.CurrentStepName ?? "外观检测";
        var result = args.Decision.Status switch
        {
            DecisionStatus.Ok => "OK",
            DecisionStatus.Ng => "NG",
            DecisionStatus.ReviewRequired => "待确认",
            _ => "检测中",
        };
        return $"{stepName}  {result}";
    }

    private static ImageSource? CloneImageSource(ImageSource? source)
    {
        if (source is not BitmapSource bitmap)
        {
            return null;
        }
        var clone = new WriteableBitmap(bitmap);
        clone.Freeze();
        return clone;
    }

    private void UpdateSopControls(SopSnapshot? snapshot)
    {
        var hasSop = snapshot is not null;
        var active = _run?.State is DetectionRunState.Running or DetectionRunState.Paused;
        // SOP 任务始终允许点击周期操作。运行状态不满足时由点击处理给出明确提示，
        // 避免按钮置灰后操作员误以为界面失效。
        StartNextSopButton.IsEnabled = hasSop && !_sopCycleActionBusy;
        ResetSopButton.IsEnabled = hasSop && !_sopCycleActionBusy;
        StartNextSopButton.ToolTip = hasSop && active
            ? "当前产品完成或失败后开始下一件；检测中点击会给出操作提示"
            : "请先启动 SOP 检测";
        ResetSopButton.ToolTip = hasSop && active
            ? "放弃当前产品并重新开始本产品周期"
            : "请先启动 SOP 检测";
        SopCycleText.Text = string.IsNullOrWhiteSpace(_run?.SopCycleId)
            ? ""
            : $"周期：{_run.SopCycleId[..Math.Min(16, _run.SopCycleId.Length)]}";
    }

    private async void StartNextSop_Click(object sender, RoutedEventArgs e)
    {
        if (_sopCycleActionBusy)
        {
            return;
        }
        if (_run is null)
        {
            ThemedMessageBox.Show("请先启动 SOP 检测。", "SOP产品周期", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sopCycleActionBusy = true;
        StartNextSopButton.Content = "处理中…";
        UpdateSopControls(_lastSopSnapshot);
        try
        {
            var snapshot = await _run.StartNextProductAsync();
            if (snapshot is not null)
            {
                UpdateSopUi(snapshot);
            }
            StatusText.Text = "已开始下一件产品，SOP 周期已重新建立。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "SOP产品周期", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _sopCycleActionBusy = false;
            StartNextSopButton.Content = "开始下一件";
            UpdateSopControls(_lastSopSnapshot);
        }
    }

    private async void ResetSop_Click(object sender, RoutedEventArgs e)
    {
        if (_sopCycleActionBusy)
        {
            return;
        }
        if (_run is null)
        {
            ThemedMessageBox.Show("请先启动 SOP 检测。", "SOP产品周期", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sopCycleActionBusy = true;
        ResetSopButton.Content = "处理中…";
        UpdateSopControls(_lastSopSnapshot);
        try
        {
            var snapshot = await _run.ResetProductAsync();
            if (snapshot is not null)
            {
                UpdateSopUi(snapshot);
            }
            StatusText.Text = "当前产品已复位，已开始新的 SOP 周期。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "SOP产品周期", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _sopCycleActionBusy = false;
            ResetSopButton.Content = "复位当前产品";
            UpdateSopControls(_lastSopSnapshot);
        }
    }

    private void OnSopChanged(SopSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateSopUi(snapshot);
            if (snapshot.Status is SopRunStatus.NgTimeout or SopRunStatus.NgConditionFailed or SopRunStatus.NgWrongOrder)
            {
                DecisionText.Text = "NG";
                DecisionText.Foreground = Brushes.Red;
            }
            else if (snapshot.Status is SopRunStatus.Interrupted or SopRunStatus.Aborted)
            {
                DecisionText.Text = "已中止";
                DecisionText.Foreground = Brushes.Gray;
            }
        });
    }

    private static bool IsSopTerminal(SopRunStatus status) => status is
        SopRunStatus.CompletedOk or SopRunStatus.NgTimeout or SopRunStatus.NgConditionFailed
        or SopRunStatus.NgWrongOrder or SopRunStatus.Interrupted or SopRunStatus.Aborted;

    private void Roi_Click(object sender, RoutedEventArgs e)
    {
        _roiDrawing = !_roiDrawing;
        _draftRoi = null;
        StatusText.Text = _roiDrawing
            ? "请在预览图像内拖拽绘制检测区域"
            : "已取消检测区域绘制";
        RenderRoi();
    }

    private void ClearRoi_Click(object sender, RoutedEventArgs e)
    {
        _roiDrawing = false;
        _roiOverrideSet = true;
        _roiOverride = null;
        _draftRoi = null;
            ApplyRoiToActiveRun();
            StatusText.Text = "已清除检测区域，将检测全图";
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        RenderRoi();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_run?.State == DetectionRunState.Paused)
        {
            await _run.ResumeAsync();
            StatusText.Text = "检测已恢复";
            SetButtons(running: true);
            return;
        }

        // 模型初始化可能需要几秒；在初始化期间禁止重复启动，避免多个 Worker
        // 并发接管同一个面板，导致预览正常但结果会话被提前关闭。
        if (_startInProgress || _run is not null)
        {
            return;
        }

        _startInProgress = true;
        SetButtons(running: false);
        try
        {
            await StartRunAsync(singleFrame: false);
        }
        finally
        {
            _startInProgress = false;
            SetButtons(running: _run is not null);
        }
    }

    private async Task StartRunAsync(bool singleFrame)
    {
        if (TaskCombo.SelectedItem is not LiveTaskItem item)
        {
            ThemedMessageBox.Show("请先在当前任务面板中选择任务。", "实时检测");
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
            ThemedMessageBox.Show("任务数据无效", "错误");
            return;
        }
        var (entity, recipe) = pair;
        recipe = recipe with { Roi = _roiOverrideSet ? _roiOverride : recipe.Roi };
            _activeRecipe = recipe;
            TaskModeText.Text = GetTaskModeText(recipe);
            SetSopLayout(recipe.Sop is not null);
            ConfigureOfflineProgress(recipe, singleFrame);
        // 离线图片单次检测按目录顺序逐张读取；处理完最后一张后，下一次点击从第一张重新开始。
        if (singleFrame
            && string.Equals(recipe.CameraProviderId, "image-folder", StringComparison.OrdinalIgnoreCase)
            && _offlineImageTotal > 0
            && _singleFrameNextIndex >= _offlineImageTotal)
        {
            _singleFrameNextIndex = 0;
            _offlineImageStartIndex = 0;
            OfflineProgressText.Text = $"离线图片：0 / {_offlineImageTotal}";
        }
        if (!singleFrame)
        {
            _singleFrameNextIndex = 0;
        }
        _lastRenderedResultSequence = -1;
        _lastRenderedPreviewSequence = -1;
        _lastOutput = null;
        _fitPreviewOnNextFrame = true;
        ClearDetectionLog();
        if (singleFrame)
        {
            ClearSingleResultDisplay();
            OverlayCanvas.Children.Clear();
            StatusText.Text = "单次检测处理中，请稍候…";
        }
        var svcs = AppServices.Instance;
        var logger = svcs.LoggerFactory.CreateLogger<LiveTaskPanel>();

        try
        {
            // 启动阶段通常已经完成预加载；若用户立即点击开始，则等待同一
            // 个预加载任务完成，避免再次创建 Worker 或重复加载模型。
            await PrepareModelAsync();
            if (_algorithmSession is null)
            {
                throw new InvalidOperationException("算法模型尚未加载");
            }

            // 1. 相机会话（虚拟源参数：间隔 200ms）
            _cameraSession = await OpenCameraAsync(recipe, CancellationToken.None, singleFrame);

            // 2. 检测运行时 + 批次：优先恢复上次异常退出遗留的 running 批次
            _run = new DetectionRunService(svcs.Records, svcs.TempImages,
                svcs.LoggerFactory.CreateLogger<DetectionRunService>(), $"task-{entity.Id}", svcs.ResultPublisher,
                () => svcs.Settings.EnableHistory,
                () => svcs.Settings.EnableHistory,
                svcs.Settings.PythonExecutable,
                svcs.SopRuns,
                pendingReplayTrigger: svcs.SopProductResultReplayer);
            var batch = await svcs.BatchService.ResumeOrStartAsync(
                entity.Id, _run.Counting, svcs.Records);
            if (!singleFrame)
            {
                _run.PreviewReceived += (_, frame) => RenderPreview(frame);
            }
            _run.RecordCompleted += OnRecordCompleted;
            _run.SopChanged += OnSopChanged;
            _run.Faulted += OnRunFaulted;
            if (!singleFrame)
            {
                _run.SourceCompleted += async (_, _) => await Dispatcher.InvokeAsync(StopFromSourceEnd);
            }
            await _run.StartAsync(
                recipe, entity.Id, _cameraSession, _algorithmSession, batch.Id,
                startProcessingLoop: !singleFrame,
                sopAlgorithms: recipe.Sop?.Definition is not null ? _sopAlgorithmSessions : null);

            UpdateStatisticsText();
            BatchText.Text = $"批次: {batch.BatchNumber}";
            StatusText.Text = singleFrame
                ? IsFiniteInputSource(recipe)
                    ? "单次检测已启动：将处理下一张离线图片。"
                    : "单次检测已启动：等待相机采集一帧。"
                : "批量检测已启动：图片/视频源将按顺序处理全部内容。";
            SetButtons(running: true);

            if (singleFrame)
            {
                await CompleteStandaloneSingleAsync(recipe, batch.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "开始检测失败");
            ThemedMessageBox.Show($"开始检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _singleFrameBusy = false;
            await CleanupAsync(disposeAlgorithm: true);
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
            StatusText.Text = "检测已暂停";
            SetButtons(running: true);
        }
        else if (_run.State == DetectionRunState.Paused)
        {
            await _run.ResumeAsync();
            StatusText.Text = "检测已恢复";
            SetButtons(running: true);
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
            _singleFrameBusy = true;
            SetButtons(running: true);
            ClearSingleResultDisplay();
            try
            {
                var result = await _run.SubmitSingleAsync(TimeSpan.FromSeconds(10));
                // OnRecordCompleted 使用 BeginInvoke 更新界面；先等待本次结果渲染完成，
                // 再解除单次检测期间的预览屏蔽，避免下一帧抢先覆盖结果。
                await Dispatcher.InvokeAsync(() => { });
                StatusText.Text = result is null
                    ? "单次检测：等待帧超时或输入源已结束"
                    : IsFiniteInputSource(_activeRecipe)
                        ? "单次检测已完成：已处理一张离线图片"
                        : "单次检测已完成：已采集一帧相机图像";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"单次检测失败：{ex.Message}";
            }
            finally
            {
                _singleFrameBusy = false;
                SetButtons(running: _run?.State is DetectionRunState.Running or DetectionRunState.Paused);
            }
            return;
        }
        await StartRunAsync(singleFrame: true);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopBatchAsync();

    /// <summary>计数清零（CNT-S-010 / CNT-L-012）：原因必填；流模式同步清 worker 跟踪记忆。</summary>
    private async void ResetCount_Click(object sender, RoutedEventArgs e)
    {
        if (_run is null)
        {
            ThemedMessageBox.Show("请先点击“开始”启动检测任务。", "清零");
            return;
        }
        var dialog = new CorrectionDialog("计数清零", hideDelta: true) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            await _run.ResetCountAsync(
                dialog.Reason, AppServices.Instance.Settings.OperatorName);
            ResetStatistics();
        }
        catch (ArgumentException ex)
        {
            ThemedMessageBox.Show(ex.Message, "校验失败");
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

    private async Task CompleteStandaloneSingleAsync(Recipe recipe, long batchId)
    {
        var result = await _run!.SubmitSingleAsync(TimeSpan.FromSeconds(30));

        // OnRecordCompleted 使用 BeginInvoke 更新结果面板；先让这次结果的 UI
        // 回调执行完，再释放单次会话，避免刚显示结果就被清理流程覆盖。
        await Dispatcher.InvokeAsync(() => { });

        if (result is not null && IsFiniteInputSource(recipe))
        {
            _singleFrameNextIndex++;
        }

        var total = _run.Counting.State.CurrentTotal;
        await _run.StopAsync();
        await AppServices.Instance.BatchService.EndAsync(total);
        await CleanupAsync();

        StatusText.Text = result is null
            ? "单次检测未获得有效帧：离线图片已处理完或相机采集超时"
            : IsFiniteInputSource(recipe)
                ? $"单次检测已完成：已处理离线图片第 {_singleFrameNextIndex} 张"
                : "单次检测已完成：已采集一帧相机图像";
    }

    private static bool IsFiniteInputSource(Recipe? recipe) =>
        recipe?.CameraProviderId is "image-folder" or "video-file";

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
        await StopBatchAsync();
    }

    private async Task CleanupAsync(bool disposeAlgorithm = false)
    {
        if (_run is not null)
        {
            _run.RecordCompleted -= OnRecordCompleted;
            _run.SopChanged -= OnSopChanged;
            _run.Faulted -= OnRunFaulted;
            await _run.DisposeAsync();
            _run = null;
        }
        if (disposeAlgorithm || _algorithmSession?.State == AlgorithmSessionState.Faulted)
        {
            await DisposePreparedModelsAsync();
        }
        if (_cameraSession is not null)
        {
            await _cameraSession.DisposeAsync();
            _cameraSession = null;
        }
        _singleFrameBusy = false;
        await Dispatcher.InvokeAsync(() => SetButtons(running: false));
    }

    private async Task DisposePreparedModelsAsync()
    {
        var sessions = _sopAlgorithmSessions.Values
            .Append(_algorithmSession)
            .Where(session => session is not null)
            .Cast<IAlgorithmSession>()
            .Distinct()
            .ToArray();
        _sopAlgorithmSessions.Clear();
        _algorithmSession = null;
        _preparedTaskId = 0;
        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private async void OnRunFaulted(object? sender, RunFaultedEventArgs e)
    {
        var reconnecting = false;
        await Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = $"故障[{e.Source}] {e.Code}: {e.Message}";
            if (e.Source == "camera"
                && e.Code == CameraErrorCodes.DeviceDisconnected
                && _run is not null
                && _activeRecipe is not null
                && Interlocked.Exchange(ref _reconnectInProgress, 1) == 0)
            {
                reconnecting = true;
                _ = ReconnectCameraAfterFaultAsync();
                return;
            }
            SetButtons(running: false);
        });

        // 算法故障后释放当前会话，保证用户可以直接重新点击“开始”，而不是
        // 留下已故障的 _run 占住面板。
        if (!reconnecting)
        {
            await CleanupAsync(disposeAlgorithm: true);
        }
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
        var options = _cameraOptions with { Parameters = recipe.CameraParameters };
        if (singleFrame && recipe.CameraProviderId is ("image-folder" or "video-file"))
        {
            options = options with { MaxFrames = 1, StartFrameIndex = _singleFrameNextIndex };
        }
        options = AppServices.Instance.ApplyCameraDefaults(descriptor, options);
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
            if (_singleFrameBusy)
            {
                return;
            }
            // 实时推理存在延迟，后续预览帧可能先于结果回调进入 UI 队列。
            // 一旦已有结果，只允许显示不超过最后结果序号的帧，保证图像和叠加层配对。
            if (_lastOutput is not null && frame.Sequence > _lastRenderedResultSequence)
            {
                return;
            }
            if (frame.Sequence < _lastRenderedPreviewSequence)
            {
                return;
            }
            _preview.Render(PreviewImage, frame);
            FitPreviewAfterSourceLoaded();
            _lastRenderedPreviewSequence = frame.Sequence;
            if (_offlineImageTotal > 0)
            {
                var currentIndex = Math.Min(
                    (long)_offlineImageTotal,
                    _offlineImageStartIndex + frame.Sequence);
            OfflineProgressText.Text = $"离线图片：{currentIndex} / {_offlineImageTotal}";
            }
        });
    }

    private void ClearSingleResultDisplay()
    {
        PreviewImage.Source = null;
        OverlayCanvas.Children.Clear();
        RoiCanvas.Children.Clear();
        _lastOutput = null;
        _lastRenderedResultSequence = -1;
        _lastRenderedPreviewSequence = -1;
        _fitPreviewOnNextFrame = true;
    }

    private void FitPreviewAfterSourceLoaded()
    {
        if (!_fitPreviewOnNextFrame || _previewFitPending || PreviewImage.Source is null)
        {
            return;
        }

        _previewFitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _previewFitPending = false;
            if (!_fitPreviewOnNextFrame || PreviewImage.Source is null)
            {
                return;
            }

            PreviewViewer.FitToWindow();
            _fitPreviewOnNextFrame = false;
            RenderRoi();
            if (_lastOutput is not null)
            {
                DrawOverlay(_lastOutput);
            }
        }));
    }

    private void ClearDetectionLog()
    {
        _detectionLog.Clear();
        if (DetectionListText is not null)
        {
            DetectionListText.Text = "暂无检测记录";
        }
    }

    private void ResetStatistics()
    {
        _okCount = 0;
        _ngCount = 0;
        UpdateStatisticsText();
    }

    private void UpdateStatisticsText()
    {
        var total = _okCount + _ngCount;
        if (TotalText is not null)
        {
            TotalText.Text = $"累计: {total}";
            OkCountText.Text = $"OK: {_okCount}";
            NgCountText.Text = $"NG: {_ngCount}";
        }
    }

    private void AppendDetectionLog(string message)
    {
        _detectionLog.Enqueue(message);
        while (_detectionLog.Count > 100)
        {
            _detectionLog.Dequeue();
        }

        DetectionListText.Text = string.Join(Environment.NewLine, _detectionLog);
        DetectionLogScrollViewer.UpdateLayout();
        DetectionLogScrollViewer.ScrollToEnd();
    }

    private void ConfigureOfflineProgress(Recipe recipe, bool singleFrame)
    {
        if (!string.Equals(recipe.CameraProviderId, "image-folder", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(recipe.CameraDeviceId))
        {
            _offlineImageTotal = 0;
            _offlineImageStartIndex = 0;
            OfflineProgressText.Visibility = Visibility.Collapsed;
            return;
        }

        _offlineImageTotal = Directory.EnumerateFiles(recipe.CameraDeviceId, "*.*", SearchOption.TopDirectoryOnly)
            .Count(file => OfflineImageExtensions.Contains(
                System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        _offlineImageStartIndex = singleFrame ? _singleFrameNextIndex : 0;
        OfflineProgressText.Text = $"离线图片：{_offlineImageStartIndex} / {_offlineImageTotal}";
        OfflineProgressText.Visibility = Visibility.Visible;
    }

    private static readonly string[] OfflineImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    private void PreviewSurface_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_roiDrawing || PreviewImage.Source is null) return;
        var point = e.GetPosition(PreviewSurface);
        if (!TryGetNormalizedPoint(point, clampToImage: false, out _roiDragStart)) return;
        _draftRoi = new NormalizedRect { X = _roiDragStart.X, Y = _roiDragStart.Y, Width = 0, Height = 0 };
        PreviewSurface.CaptureMouse();
        RenderRoi();
        e.Handled = true;
    }

    private void PreviewSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_roiDrawing || !PreviewSurface.IsMouseCaptured || PreviewImage.Source is null) return;
        var point = e.GetPosition(PreviewSurface);
        if (!TryGetNormalizedPoint(point, clampToImage: true, out var end)) return;
        _draftRoi = CreateNormalizedRect(_roiDragStart, end);
        RenderRoi();
        e.Handled = true;
    }

    private void PreviewSurface_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_roiDrawing || !PreviewSurface.IsMouseCaptured) return;
        PreviewSurface.ReleaseMouseCapture();
        if (_draftRoi is { Width: > 0.005, Height: > 0.005 } roi)
        {
            _roiOverrideSet = true;
            _roiOverride = roi;
            _roiDrawing = false;
            ApplyRoiToActiveRun();
            StatusText.Text = "检测区域已设置，区域外不参与检测";
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _draftRoi = null;
            StatusText.Text = "检测区域太小，未保存";
        }
        RenderRoi();
        e.Handled = true;
    }

    private void ApplyRoiToActiveRun()
    {
        if (_activeRecipe is not { } recipe) return;
        var roi = _roiOverrideSet ? _roiOverride : recipe.Roi;
        _activeRecipe = recipe with { Roi = roi };
        _run?.UpdateRoi(roi);
    }

    private bool TryGetNormalizedPoint(Point point, bool clampToImage, out Point normalized)
    {
        normalized = default;
        if (PreviewImage.Source is null || PreviewSurface.ActualWidth <= 0 || PreviewSurface.ActualHeight <= 0)
            return false;
        var sourceWidth = PreviewImage.Source.Width;
        var sourceHeight = PreviewImage.Source.Height;
        var scale = Math.Min(PreviewSurface.ActualWidth / sourceWidth, PreviewSurface.ActualHeight / sourceHeight);
        var drawWidth = sourceWidth * scale;
        var drawHeight = sourceHeight * scale;
        var offsetX = (PreviewSurface.ActualWidth - drawWidth) / 2;
        var offsetY = (PreviewSurface.ActualHeight - drawHeight) / 2;
        var x = (point.X - offsetX) / drawWidth;
        var y = (point.Y - offsetY) / drawHeight;
        if (!clampToImage && (x < 0 || x > 1 || y < 0 || y > 1)) return false;
        normalized = new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        return true;
    }

    private static NormalizedRect CreateNormalizedRect(Point start, Point end) => new()
    {
        X = Math.Min(start.X, end.X),
        Y = Math.Min(start.Y, end.Y),
        Width = Math.Abs(end.X - start.X),
        Height = Math.Abs(end.Y - start.Y),
    };

    private void RenderRoi()
    {
        RoiCanvas.Children.Clear();
        var roi = _draftRoi ?? (_roiOverrideSet ? _roiOverride : _activeRecipe?.Roi);
        if (roi is null || PreviewImage.Source is null || RoiCanvas.ActualWidth <= 0 || RoiCanvas.ActualHeight <= 0)
            return;
        var sourceWidth = PreviewImage.Source.Width;
        var sourceHeight = PreviewImage.Source.Height;
        var scale = Math.Min(RoiCanvas.ActualWidth / sourceWidth, RoiCanvas.ActualHeight / sourceHeight);
        var drawWidth = sourceWidth * scale;
        var drawHeight = sourceHeight * scale;
        var offsetX = (RoiCanvas.ActualWidth - drawWidth) / 2;
        var offsetY = (RoiCanvas.ActualHeight - drawHeight) / 2;
        var rectangle = new Rectangle
        {
            Width = Math.Max(1, roi.Width * drawWidth),
            Height = Math.Max(1, roi.Height * drawHeight),
            Stroke = _roiDrawing ? Brushes.Gold : Brushes.DodgerBlue,
            StrokeThickness = 2,
            StrokeDashArray = [6, 3],
            Fill = new SolidColorBrush(Color.FromArgb(24, 30, 144, 255)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rectangle, offsetX + roi.X * drawWidth);
        Canvas.SetTop(rectangle, offsetY + roi.Y * drawHeight);
        RoiCanvas.Children.Add(rectangle);
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderRoi();
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
            var resultSequence = e.Frame?.Sequence ?? e.Output.Sequence;
            if (resultSequence < _lastRenderedResultSequence)
            {
                return;
            }
            _lastOutput = e.Output;
            _lastRenderedResultSequence = resultSequence;
            if (e.Sop is not null)
            {
                ShowPreviousSopResult();
                UpdateSopUi(e.Sop);
                _lastSopRoundText = BuildSopRoundText(e);
            }
            var (text, color) = GetDisplayDecision(e);
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
            var detectionSummary = behaviorResults.Length > 0
                ? string.Join("、", behaviorResults)
                : string.Join("、", e.Output.Detections.Take(8)
                    .Select(d => $"{d.ClassName} {d.Confidence:0.00}"));
            AppendDetectionLog(
                $"{DateTime.Now:HH:mm:ss.fff}  帧 {e.Frame?.Sequence ?? e.Output.Sequence}  "
                + $"工位 {e.StationCode}  {text}  数量 {e.Output.GetCount()}"
                + (string.IsNullOrWhiteSpace(detectionSummary) ? "" : $"  {detectionSummary}"));
            switch (e.Decision.Status)
            {
                case DecisionStatus.Ok: _okCount++; break;
                case DecisionStatus.Ng: _ngCount++; break;
            }
            UpdateStatisticsText();

            // 结果必须绘制在本次推理对应的原始帧上，避免批量处理时错叠到下一张图片。
            if (e.Frame is not null)
            {
                _preview.Render(PreviewImage, e.Frame, force: true);
                FitPreviewAfterSourceLoaded();
                if (e.Sop is not null)
                {
                    _lastSopRoundImage = CloneImageSource(PreviewImage.Source);
                }
                _lastRenderedPreviewSequence = e.Frame.Sequence;
                RenderRoi();
                if (_offlineImageTotal > 0)
                {
                    var currentIndex = Math.Min(
                        (long)_offlineImageTotal,
                        _offlineImageStartIndex + e.Frame.Sequence);
                    OfflineProgressText.Text = $"离线图片：{currentIndex} / {_offlineImageTotal}";
                }
            }
            DrawOverlay(e.Output);
        });
    }

    private static (string Text, Brush Brush) GetDisplayDecision(RecordCompletedEventArgs args)
    {
        if (args.Sop is { } sop)
        {
            return args.Decision.Status switch
            {
                DecisionStatus.Ok when sop.Status == SopRunStatus.CompletedOk => ("OK", Brushes.Green),
                DecisionStatus.Ng => ("NG", Brushes.Red),
                DecisionStatus.ReviewRequired => ("待确认", Brushes.Orange),
                DecisionStatus.Unknown when sop.Status is SopRunStatus.Interrupted or SopRunStatus.Aborted
                    => ("已中止", Brushes.Gray),
                _ => ("检测中", Brushes.DodgerBlue),
            };
        }

        return args.Decision.Status switch
        {
            DecisionStatus.Ok => ("OK", Brushes.Green),
            DecisionStatus.Ng => ("NG", Brushes.Red),
            DecisionStatus.ReviewRequired => ("待确认", Brushes.Orange),
            DecisionStatus.Processing or DecisionStatus.Unknown => ("检测中", Brushes.DodgerBlue),
            _ => ("错误", Brushes.Gray),
        };
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
                X1 = Px(cfg.A.X),
                Y1 = Py(cfg.A.Y),
                X2 = Px(cfg.B.X),
                Y2 = Py(cfg.B.Y),
                Stroke = Brushes.Yellow,
                StrokeThickness = 2,
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
        if (_lastSopSnapshot is not null && SopCard.Visibility == Visibility.Visible)
        {
            UpdateSopMetrics(_lastSopSnapshot);
        }
        var svcs = AppServices.Instance;
        var provider = _activeRecipe?.ExecutionProvider ?? svcs.Settings.ExecutionProvider;
        StatusText.Text =
            $"相机 FPS: {_preview.Fps:0.#} | 算法 FPS: {_algoFps:0.#} | 后端: {provider}"
            + $" | 数据库: {System.IO.Path.Combine(svcs.Settings.ConfigDirectory, "visionworkbench.db")}"
            + (_run is null ? "" : $" | 丢帧: {_run.Scheduler.DroppedFrameCount}");
    }

    private void SetButtons(bool running)
    {
        // 开始与暂停始终互斥：初始化/停止时只能开始，运行时只能暂停。
        var paused = running && _run?.State == DetectionRunState.Paused;
        var canStart = (!running || paused) && !_startInProgress;
        var canPause = running && !paused && !_startInProgress;
        StartButton.IsEnabled = canStart;
        PauseButton.IsEnabled = canPause;
        PauseButton.Content = "暂停";
        SingleButton.IsEnabled = !_singleFrameBusy && (!_startInProgress || running);
        StopButton.IsEnabled = running;
        ResetCountButton.IsEnabled = true;
        TaskCombo.IsEnabled = !running && !_startInProgress;
        UpdateSopControls(_run?.Sop);
    }

}
