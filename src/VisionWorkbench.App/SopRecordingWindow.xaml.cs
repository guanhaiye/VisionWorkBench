using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

/// <summary>独立的 SOP 参考视频录制窗口：按步骤提示动作并在总时长结束后自动保存。</summary>
public partial class SopRecordingPage : UserControl
{
    private sealed class RecordingStepRow : INotifyPropertyChanged
    {
        private string _name = "";
        private string _durationText = "5";

        public int Order { get; set; }
        public string Name
        {
            get => _name;
            set
            {
                if (string.Equals(_name, value, StringComparison.Ordinal)) return;
                _name = value;
                OnPropertyChanged();
            }
        }
        public string DurationText
        {
            get => _durationText;
            set
            {
                if (string.Equals(_durationText, value, StringComparison.Ordinal)) return;
                _durationText = value;
                OnPropertyChanged();
                DurationChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? DurationChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class RecordingPlan
    {
        public string DefinitionId { get; set; } = "";
        public List<RecordingPlanStep> Steps { get; set; } = [];
    }

    private sealed class RecordingPlanStep
    {
        public string Name { get; set; } = "";
        public double DurationSeconds { get; set; } = 5;
    }

    private sealed class SopVideoRecord
    {
        public string Id { get; set; } = "";
        public string DefinitionId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string RelativeVideoPath { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public double DurationSeconds { get; set; }
        public int FrameCount { get; set; }
    }

    private readonly SopDefinition _definition;
    private readonly ObservableCollection<RecordingStepRow> _steps = [];
    private readonly ObservableCollection<SopVideoRecord> _videos = [];
    private readonly object _captureLock = new();
    private readonly PreviewRenderer _previewRenderer = new();
    private readonly DispatcherTimer _videoProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ICameraSession? _cameraSession;
    private VideoWriter? _writer;
    private string? _videoPath;
    private string? _framesDirectory;
    private string? _recordId;
    private DateTimeOffset? _recordStartedAt;
    private int _frameCount;
    private bool _recording;
    private bool _videoIsPlaying;
    private SopVideoRecord? _previewedVideoRecord;
    private bool _toggleSelectedVideoOnClick;
    private CancellationTokenSource? _scheduleCancellation;
    private int _generation;
    private bool _manualMode;
    private TaskCompletionSource<bool>? _manualStepStartSignal;

    private string PlanIndexPath => Path.Combine(AppServices.Instance.Settings.ConfigDirectory, "sop-recording-plans.json");
    private string VideoIndexPath => Path.Combine(AppServices.Instance.Settings.ConfigDirectory, "sop-video-recordings.json");
    private string VideoDirectory => Path.Combine(AppServices.Instance.Settings.DataDirectory, "sop-recordings", SafeFileName(_definition.Code));

    public SopRecordingPage(SopDefinition definition)
    {
        _definition = definition;
        InitializeComponent();
        _videoProgressTimer.Tick += VideoProgressTimer_Tick;
        StepGrid.ItemsSource = _steps;
        VideoList.ItemsSource = _videos;
        SopNameText.Text = $"{definition.Name}  ·  {definition.Code}  ·  v{definition.Version}";

        foreach (var step in definition.Steps.OrderBy(item => item.Order))
        {
            AddStepRow(step.Name, step.RecordingDurationSeconds);
        }
        UpdateTotalDuration();
    }

    private async void Control_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadPlanAsync();
        await RefreshCamerasAsync();
        await LoadVideosAsync();
    }

    private async void Control_Unloaded(object? sender, RoutedEventArgs e)
    {
        _videoProgressTimer.Stop();
        await StopRecordingAsync(saveRecord: true);
        await StopCameraAsync();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        await StopRecordingAsync(saveRecord: true);
        await StopCameraAsync();
        if (System.Windows.Window.GetWindow(this) is Shell shell)
            shell.NavigateToSopPage();
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        AddStepRow($"步骤{_steps.Count + 1}", 5);
        UpdateTotalDuration();
        _ = SavePlanAsync();
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepGrid.SelectedItem is RecordingStepRow selected)
            _steps.Remove(selected);
        else if (_steps.Count > 0)
            _steps.RemoveAt(_steps.Count - 1);

        RenumberSteps();
        UpdateTotalDuration();
        _ = SavePlanAsync();
    }

    private void AddStepRow(string name, double durationSeconds)
    {
        var row = new RecordingStepRow
        {
            Order = _steps.Count + 1,
            Name = string.IsNullOrWhiteSpace(name) ? $"步骤{_steps.Count + 1}" : name,
            DurationText = durationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
        };
        row.DurationChanged += Step_DurationChanged;
        _steps.Add(row);
    }

    private void Step_DurationChanged(object? sender, EventArgs e)
    {
        UpdateTotalDuration();
        if (_scheduleCancellation is null) _ = SavePlanAsync();
    }

    private void RenumberSteps()
    {
        for (var index = 0; index < _steps.Count; index++)
            _steps[index].Order = index + 1;
    }

    private void UpdateTotalDuration()
    {
        var total = 0d;
        var valid = true;
        foreach (var row in _steps)
        {
            if (!double.TryParse(row.DurationText.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                valid = false;
                break;
            }
            total += seconds;
        }
        TotalDurationText.Text = valid
            ? $"总时长：{total:0.0} 秒"
            : "总时长：请检查持续时间";
    }

    private async Task RefreshCamerasAsync()
    {
        try
        {
            var previous = CameraCombo.SelectedItem as CameraDescriptor;
            var cameras = await AppServices.Instance.Cameras.DiscoverAllAsync(CancellationToken.None);
            CameraCombo.ItemsSource = cameras;
            CameraCombo.SelectedItem = cameras.FirstOrDefault(item =>
                previous is not null
                && string.Equals(item.ProviderId, previous.ProviderId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DeviceId, previous.DeviceId, StringComparison.OrdinalIgnoreCase))
                ?? cameras.FirstOrDefault();
            RecordStatusText.Text = cameras.Count == 0 ? "未发现可用相机" : $"发现 {cameras.Count} 个相机，请点击“开始录制”";
        }
        catch (Exception ex)
        {
            RecordStatusText.Text = $"相机扫描失败：{ex.Message}";
        }
    }

    private async void RefreshCamera_Click(object sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private void RecordModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scheduleCancellation is not null) return;
        ApplyRecordingModeUi();
    }

    private void ApplyRecordingModeUi()
    {
        if (ManualStepButton is null) return;
        var manual = RecordModeCombo.SelectedIndex == 1;
        ManualStepButton.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ManualStepButton.IsEnabled = false;
        ManualStepButton.Content = "开始当前步骤";
    }

    private void ManualStepButton_Click(object sender, RoutedEventArgs e)
    {
        ManualStepButton.IsEnabled = false;
        _manualStepStartSignal?.TrySetResult(true);
    }

    private bool TryGetSchedule(out List<(string Name, double Seconds)> schedule, out string message)
    {
        schedule = [];
        message = "";
        foreach (var row in _steps)
        {
            var name = row.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                message = "动作步骤名称不能为空。";
                return false;
            }
            if (!double.TryParse(row.DurationText.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                message = $"步骤“{name}”的持续时间必须大于 0 秒。";
                return false;
            }
            schedule.Add((name, seconds));
        }
        if (schedule.Count == 0)
        {
            message = "请先添加至少一个录制步骤。";
            return false;
        }
        return true;
    }

    private async void RecordToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduleCancellation is not null || _recording)
        {
            await StopRecordingAsync(saveRecord: true);
            return;
        }
        if (!TryGetSchedule(out var schedule, out var message))
        {
            ThemedMessageBox.Show(message, "SOP视频录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CameraCombo.SelectedItem is not CameraDescriptor)
        {
            ThemedMessageBox.Show("请先选择相机。", "SOP视频录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await StartCameraAsync();
            Directory.CreateDirectory(VideoDirectory);
            _recordId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            _videoPath = Path.Combine(VideoDirectory, $"{_recordId}.avi");
            _framesDirectory = Path.Combine(VideoDirectory, _recordId);
            _recordStartedAt = null;
            _frameCount = 0;
            _writer = null;
            _scheduleCancellation = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _generation);
            _manualMode = RecordModeCombo.SelectedIndex == 1;
            RecordModeCombo.IsEnabled = false;
            RecordToggleButton.Content = "停止录制";
            StepGrid.IsEnabled = false;
            VideoPlayer.Stop();
            VideoPlayer.Close();
            VideoPlayer.Source = null;
            VideoPlayer.Visibility = Visibility.Collapsed;
            VideoPlaybackControls.Visibility = Visibility.Collapsed;
            _previewedVideoRecord = null;
            PreviewHintText.Visibility = Visibility.Collapsed;
            StepPromptText.Text = "准备开始";
            SetCountdownPlainText(string.Empty);
            RecordStatusText.Text = "录制准备中";
            _ = RunScheduleAsync(schedule, generation, _scheduleCancellation.Token);
        }
        catch (Exception ex)
        {
            RecordToggleButton.Content = "开始录制";
            RecordStatusText.Text = $"录制启动失败：{ex.Message}";
            await StopCameraAsync();
        }
    }

    private async Task StartCameraAsync()
    {
        if (_cameraSession is not null) return;
        if (CameraCombo.SelectedItem is not CameraDescriptor descriptor)
            throw new InvalidOperationException("未选择相机");

        var options = AppServices.Instance.ApplyCameraDefaults(descriptor,
            new CameraOpenOptions { DesiredFps = 10 });
        var session = await AppServices.Instance.Cameras.OpenSessionAsync(descriptor, options, CancellationToken.None);
        try
        {
            session.FrameReceived += Camera_FrameReceived;
            session.Faulted += Camera_Faulted;
            await session.OpenAsync(options, CancellationToken.None);
            await session.StartAsync(CancellationToken.None);
            _cameraSession = session;
            session = null!;
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
        }
    }

    private void Camera_Faulted(object? sender, CameraFaultedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RecordStatusText.Text = $"相机故障：{e.Fault.Message}";
            if (_recording || _scheduleCancellation is not null) _ = StopRecordingAsync(saveRecord: true);
        });
    }

    private void Camera_FrameReceived(object? sender, VideoFrameReceivedEventArgs e)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
            new Action(() => _previewRenderer.Render(PreviewImage, e.Frame, maxFps: 15, force: true)));

        lock (_captureLock)
        {
            if (!_recording || _videoPath is null || _framesDirectory is null) return;
            try
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(_framesDirectory);
                    _writer = CreateWriter(_videoPath, 10, new OpenCvSharp.Size(e.Frame.Width, e.Frame.Height));
                    if (_writer is null || !_writer.IsOpened())
                        throw new InvalidOperationException("无法创建视频文件，请检查磁盘空间和视频编码器。");
                }
                using var mat = Mat.FromPixelData(e.Frame.Height, e.Frame.Width,
                    MatType.CV_8UC3, e.Frame.Pixels, e.Frame.Stride);
                _writer.Write(mat);
                Cv2.ImWrite(Path.Combine(_framesDirectory, $"frame_{_frameCount:000000}.jpg"), mat);
                _frameCount++;
            }
            catch (Exception ex)
            {
                _recording = false;
                Dispatcher.BeginInvoke(() => RecordStatusText.Text = $"视频保存失败：{ex.Message}");
            }
        }
    }

    private async Task RunScheduleAsync(List<(string Name, double Seconds)> schedule,
        int generation, CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; index < schedule.Count; index++)
            {
                var step = schedule[index];
                lock (_captureLock) _recording = false;
                await PrepareStepAsync(step.Name, index, schedule.Count, cancellationToken);
                HidePreparationOverlay();

                if (_manualMode)
                {
                    _manualStepStartSignal = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StepPromptText.Text = $"请点击按钮开始：{step.Name}";
                        SetCountdownPlainText($"第 {index + 1}/{schedule.Count} 步 · 等待开始");
                        ManualStepButton.Content = $"开始第 {index + 1} 步录制";
                        ManualStepButton.IsEnabled = true;
                    });
                    await _manualStepStartSignal.Task.WaitAsync(cancellationToken);
                    _manualStepStartSignal = null;
                }

                lock (_captureLock)
                {
                    _recording = true;
                    _recordStartedAt ??= DateTimeOffset.Now;
                }
                SetPrompt($"请执行：{step.Name}", $"第 {index + 1}/{schedule.Count} 步 · 录制中");
                var end = DateTimeOffset.UtcNow.AddSeconds(step.Seconds);
                while (DateTimeOffset.UtcNow < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = Math.Max(0, (end - DateTimeOffset.UtcNow).TotalSeconds);
                    SetPromptWithRemaining($"请执行：{step.Name}",
                        $"第 {index + 1}/{schedule.Count} 步 · 剩余 ", remaining);
                    await Task.Delay(200, cancellationToken);
                }
            }

            SetPrompt("录制完成", "正在保存视频…");
            if (generation == Volatile.Read(ref _generation))
                await Dispatcher.InvokeAsync(() => StopRecordingAsync(saveRecord: true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PrepareStepAsync(string stepName, int index, int count,
        CancellationToken cancellationToken)
    {
        var preparationSeconds = 3;
        if (int.TryParse(PreparationSecondsText.Text.Trim(), out var configured))
            preparationSeconds = Math.Clamp(configured, 0, 30);
        for (var remaining = preparationSeconds; remaining > 0; remaining--)
        {
            SetPrompt("准备录制", $"第 {index + 1}/{count} 步准备中");
            SetPreparationPrompt($"请准备第 {index + 1}/{count} 步：{stepName}", remaining);
            await Task.Delay(1000, cancellationToken);
        }
    }

    private void SetPreparationPrompt(string prompt, int remaining)
        => Dispatcher.BeginInvoke(() =>
        {
            PreparationOverlay.Visibility = Visibility.Visible;
            PreparationPromptText.Text = prompt;
            PreparationCountdownNumberRun.Text = remaining.ToString(CultureInfo.InvariantCulture);
        });

    private void HidePreparationOverlay()
        => Dispatcher.BeginInvoke(() => PreparationOverlay.Visibility = Visibility.Collapsed);

    private async Task RunScheduleLegacyAsync(List<(string Name, double Seconds)> schedule,
        int generation, CancellationToken cancellationToken)
    {
        try
        {
            var preparationSeconds = 3;
            if (int.TryParse(PreparationSecondsText.Text.Trim(), out var configured))
                preparationSeconds = Math.Clamp(configured, 0, 30);

            for (var remaining = preparationSeconds; remaining > 0; remaining--)
            {
                SetPrompt("准备开始，请保持画面稳定", $"{remaining} 秒后开始");
                await Task.Delay(1000, cancellationToken);
            }

            lock (_captureLock)
            {
                _recording = true;
                _recordStartedAt = DateTimeOffset.Now;
            }

            for (var index = 0; index < schedule.Count; index++)
            {
                var step = schedule[index];
                var end = DateTimeOffset.UtcNow.AddSeconds(step.Seconds);
                while (DateTimeOffset.UtcNow < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = Math.Max(0, (end - DateTimeOffset.UtcNow).TotalSeconds);
                    SetPromptWithRemaining($"请执行：{step.Name}",
                        $"第 {index + 1}/{schedule.Count} 步 · 剩余 ", remaining);
                    await Task.Delay(200, cancellationToken);
                }
            }

            SetPrompt("录制完成", "正在保存视频…");
            if (generation == Volatile.Read(ref _generation))
                await Dispatcher.InvokeAsync(() => StopRecordingAsync(saveRecord: true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetPrompt(string prompt, string countdown)
        => Dispatcher.BeginInvoke(() =>
        {
            StepPromptText.Text = prompt;
            SetCountdownPlainText(countdown);
        });

    private void SetPromptWithRemaining(string prompt, string prefix, double remaining)
        => Dispatcher.BeginInvoke(() =>
        {
            StepPromptText.Text = prompt;
            CountdownPlainRun.Text = string.Empty;
            CountdownRemainingPrefixRun.Text = prefix;
            CountdownRemainingValueRun.Text = Math.Ceiling(remaining).ToString("0", CultureInfo.InvariantCulture);
            CountdownRemainingUnitRun.Text = "秒";
        });

    private void SetCountdownPlainText(string text)
    {
        CountdownPlainRun.Text = text;
        CountdownRemainingPrefixRun.Text = string.Empty;
        CountdownRemainingValueRun.Text = string.Empty;
        CountdownRemainingUnitRun.Text = string.Empty;
    }

    private async Task StopRecordingAsync(bool saveRecord)
    {
        _scheduleCancellation?.Cancel();
        _scheduleCancellation?.Dispose();
        _scheduleCancellation = null;
        _manualStepStartSignal?.TrySetCanceled();
        _manualStepStartSignal = null;
        SopVideoRecord? record = null;

        lock (_captureLock)
        {
            _recording = false;
            _writer?.Release();
            _writer?.Dispose();
            _writer = null;
            if (saveRecord && _recordId is not null && _videoPath is not null
                && _frameCount >= 2 && File.Exists(_videoPath))
            {
                var relativePath = Path.GetRelativePath(AppServices.Instance.Settings.DataDirectory, _videoPath);
                record = new SopVideoRecord
                {
                    Id = _recordId,
                    DefinitionId = _definition.Id,
                    DisplayName = $"{_recordStartedAt:yyyy-MM-dd HH:mm:ss} · {_frameCount} 帧",
                    RelativeVideoPath = relativePath,
                    StartedAt = _recordStartedAt ?? DateTimeOffset.Now,
                    DurationSeconds = _frameCount / 10d,
                    FrameCount = _frameCount,
                };
            }
        }

        _recordId = null;
        _videoPath = null;
        _framesDirectory = null;
        _recordStartedAt = null;
        PreparationOverlay.Visibility = Visibility.Collapsed;
        RecordToggleButton.Content = "开始录制";
        StepGrid.IsEnabled = true;
        RecordModeCombo.IsEnabled = true;
        ApplyRecordingModeUi();
        SetCountdownPlainText(string.Empty);

        if (record is not null)
        {
            var allRecords = await ReadVideoRecordsAsync();
            allRecords.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
            allRecords.Add(record);
            Directory.CreateDirectory(Path.GetDirectoryName(VideoIndexPath)!);
            await File.WriteAllTextAsync(VideoIndexPath,
                JsonSerializer.Serialize(allRecords, new JsonSerializerOptions { WriteIndented = true }));
            _videos.Insert(0, record);
            VideoStatusText.Text = $"已保存：{record.DisplayName}";
            StepPromptText.Text = "录制完成，可再次录制";
        }
        else if (saveRecord)
        {
            VideoStatusText.Text = "录制已停止，未生成有效视频";
            StepPromptText.Text = "录制已停止";
        }
    }

    private async Task StopCameraAsync()
    {
        var session = _cameraSession;
        _cameraSession = null;
        if (session is null) return;
        session.FrameReceived -= Camera_FrameReceived;
        session.Faulted -= Camera_Faulted;
        try { await session.StopAsync(CancellationToken.None); } catch { }
        try { await session.DisposeAsync(); } catch { }
    }

    private async Task LoadPlanAsync()
    {
        try
        {
            if (!File.Exists(PlanIndexPath)) return;
            var plans = JsonSerializer.Deserialize<List<RecordingPlan>>(
                await File.ReadAllTextAsync(PlanIndexPath)) ?? [];
            var plan = plans.LastOrDefault(item => string.Equals(item.DefinitionId, _definition.Id, StringComparison.Ordinal));
            if (plan is null || plan.Steps.Count == 0) return;
            foreach (var row in _steps) row.DurationChanged -= Step_DurationChanged;
            _steps.Clear();
            foreach (var step in plan.Steps) AddStepRow(step.Name, step.DurationSeconds);
            UpdateTotalDuration();
        }
        catch (Exception ex)
        {
            VideoStatusText.Text = $"录制步骤加载失败：{ex.Message}";
        }
    }

    private async Task SavePlanAsync()
    {
        try
        {
            var plans = File.Exists(PlanIndexPath)
                ? JsonSerializer.Deserialize<List<RecordingPlan>>(await File.ReadAllTextAsync(PlanIndexPath)) ?? []
                : [];
            plans.RemoveAll(item => string.Equals(item.DefinitionId, _definition.Id, StringComparison.Ordinal));
            plans.Add(new RecordingPlan
            {
                DefinitionId = _definition.Id,
                Steps = _steps.Select(item => new RecordingPlanStep
                {
                    Name = item.Name.Trim(),
                    DurationSeconds = double.TryParse(item.DurationText.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5,
                }).ToList(),
            });
            Directory.CreateDirectory(Path.GetDirectoryName(PlanIndexPath)!);
            await File.WriteAllTextAsync(PlanIndexPath,
                JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 步骤配置不应阻断录像流程，录制启动时仍会重新校验输入。
        }
    }

    private async Task LoadVideosAsync()
    {
        _videos.Clear();
        foreach (var record in (await ReadVideoRecordsAsync())
                     .Where(item => string.Equals(item.DefinitionId, _definition.Id, StringComparison.Ordinal))
                     .OrderByDescending(item => item.StartedAt))
            _videos.Add(record);
        VideoStatusText.Text = _videos.Count == 0 ? "暂无已录制视频" : $"已有 {_videos.Count} 个视频，选择后可以检查";
    }

    private async Task<List<SopVideoRecord>> ReadVideoRecordsAsync()
    {
        try
        {
            if (File.Exists(VideoIndexPath))
                return JsonSerializer.Deserialize<List<SopVideoRecord>>(
                    await File.ReadAllTextAsync(VideoIndexPath)) ?? [];
        }
        catch
        {
        }
        return [];
    }

    private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RestoreMainPreview();
        if (VideoList.SelectedItem is not SopVideoRecord record) return;
        ShowVideoRecord(record);
    }

    private void ShowVideoRecord(SopVideoRecord record)
    {
        var path = Path.Combine(AppServices.Instance.Settings.DataDirectory, record.RelativeVideoPath);
        if (!File.Exists(path))
        {
            StepPromptText.Text = "视频文件不存在";
            RecordStatusText.Text = "该视频可能已被移动或删除";
            VideoStatusText.Text = "视频文件不存在，可能已被移动或删除";
            return;
        }
        VideoPlayer.Source = new Uri(path);
        VideoPlayer.Visibility = Visibility.Visible;
        VideoPlaybackControls.Visibility = Visibility.Visible;
        PreviewHintText.Visibility = Visibility.Collapsed;
        _previewedVideoRecord = record;
        StepPromptText.Text = $"正在查看视频：{record.DisplayName}";
        SetCountdownPlainText("再次点击当前视频可恢复相机预览");
        RecordStatusText.Text = "视频预览已加载";
        VideoStatusText.Text = $"{record.DisplayName} · 时长约 {record.DurationSeconds:0.0} 秒";
    }

    private void VideoList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(VideoList, e.OriginalSource as DependencyObject)
                   as ListBoxItem;
        _toggleSelectedVideoOnClick = item?.DataContext is SopVideoRecord record
            && ReferenceEquals(VideoList.SelectedItem, record);
    }

    private void VideoList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_toggleSelectedVideoOnClick) return;
        _toggleSelectedVideoOnClick = false;
        if (VideoList.SelectedItem is not SopVideoRecord record) return;
        if (_previewedVideoRecord is null)
            ShowVideoRecord(record);
        else
            RestoreMainPreview();
    }

    private void RestoreMainPreview()
    {
        VideoPlayer.Stop();
        VideoPlayer.Close();
        VideoPlayer.Source = null;
        _videoProgressTimer.Stop();
        ResetVideoProgress();
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoPlaybackControls.Visibility = Visibility.Collapsed;
        _previewedVideoRecord = null;
        PreviewHintText.Visibility = _cameraSession is null ? Visibility.Visible : Visibility.Collapsed;
        StepPromptText.Text = _steps.Count == 0 ? "请在左侧添加录制步骤" : "请选择相机后点击“开始录制”";
        SetCountdownPlainText(string.Empty);
        RecordStatusText.Text = "选择相机后点击“开始录制”";
    }

    private void VideoPlayer_MediaOpened(object? sender, RoutedEventArgs e)
    {
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoProgressSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            VideoDurationText.Text = FormatVideoTime(VideoPlayer.NaturalDuration.TimeSpan);
        }
        PreviewHintText.Visibility = Visibility.Collapsed;
        VideoPlayer.Play();
        _videoIsPlaying = true;
        UpdateVideoPlayPauseButton();
        _videoProgressTimer.Start();
    }

    private void VideoProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (!VideoPlayer.NaturalDuration.HasTimeSpan) return;

        var position = VideoPlayer.Position;
        if (!_isVideoSeeking)
        {
            VideoProgressSlider.Value = Math.Clamp(
                position.TotalSeconds, VideoProgressSlider.Minimum, VideoProgressSlider.Maximum);
        }
        VideoCurrentTimeText.Text = FormatVideoTime(position);
    }

    private bool _isVideoSeeking;

    private void VideoProgressSlider_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isVideoSeeking = true;
    }

    private void VideoProgressSlider_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SeekVideoToSlider();
        _isVideoSeeking = false;
    }

    private void VideoProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isVideoSeeking) SeekVideoToSlider();
    }

    private void SeekVideoToSlider()
    {
        if (!VideoPlayer.NaturalDuration.HasTimeSpan) return;
        VideoPlayer.Position = TimeSpan.FromSeconds(VideoProgressSlider.Value);
        VideoCurrentTimeText.Text = FormatVideoTime(VideoPlayer.Position);
    }

    private void ResetVideoProgress()
    {
        _isVideoSeeking = false;
        _videoIsPlaying = false;
        VideoProgressSlider.Maximum = 1;
        VideoProgressSlider.Value = 0;
        VideoCurrentTimeText.Text = "00:00";
        VideoDurationText.Text = "00:00";
        UpdateVideoPlayPauseButton();
    }

    private static string FormatVideoTime(TimeSpan time)
        => time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss");

    private void VideoPlayer_MediaEnded(object? sender, RoutedEventArgs e)
    {
        _videoProgressTimer.Stop();
        _videoIsPlaying = false;
        UpdateVideoPlayPauseButton();
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoProgressSlider.Value = VideoProgressSlider.Maximum;
            VideoCurrentTimeText.Text = FormatVideoTime(VideoPlayer.NaturalDuration.TimeSpan);
        }
    }

    private void VideoList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(VideoList, e.OriginalSource as DependencyObject)
                   as ListBoxItem;
        if (item?.DataContext is SopVideoRecord record)
        {
            VideoList.SelectedItem = record;
            return;
        }

        e.Handled = true;
    }

    private void OpenVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VideoList.SelectedItem is not SopVideoRecord record) return;

        var path = Path.Combine(AppServices.Instance.Settings.DataDirectory, record.RelativeVideoPath);
        if (!File.Exists(path))
        {
            VideoStatusText.Text = "视频文件不存在，可能已被移动或删除";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    private async void DeleteVideo_Click(object sender, RoutedEventArgs e)
    {
        if (VideoList.SelectedItem is not SopVideoRecord record) return;

        var result = MessageBox.Show(
            $"确定删除视频“{record.DisplayName}”吗？\n视频文件和对应记录都会被删除。",
            "确认删除视频", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await RemoveVideoRecordsAsync([record]);
    }

    private async void ClearVideos_Click(object sender, RoutedEventArgs e)
    {
        if (_videos.Count == 0) return;

        var result = MessageBox.Show(
            $"确定清空当前页面的 {_videos.Count} 个视频吗？\n视频文件和对应记录都会被删除。",
            "确认清空视频", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await RemoveVideoRecordsAsync(_videos.ToList());
    }

    private async Task RemoveVideoRecordsAsync(IReadOnlyCollection<SopVideoRecord> records)
    {
        VideoPlayer.Stop();
        VideoPlayer.Close();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoPlaybackControls.Visibility = Visibility.Collapsed;
        PreviewHintText.Visibility = Visibility.Visible;

        var failed = new List<SopVideoRecord>();
        foreach (var record in records)
        {
            try
            {
                DeleteVideoFiles(record);
            }
            catch
            {
                failed.Add(record);
            }
        }

        var ids = records.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var allRecords = await ReadVideoRecordsAsync();
        allRecords.RemoveAll(item =>
            string.Equals(item.DefinitionId, _definition.Id, StringComparison.Ordinal)
            && ids.Contains(item.Id)
            && !failed.Any(failedItem => string.Equals(failedItem.Id, item.Id, StringComparison.Ordinal)));
        await SaveVideoRecordsAsync(allRecords);
        await LoadVideosAsync();

        if (failed.Count > 0)
        {
            MessageBox.Show(
                $"有 {failed.Count} 个视频无法删除，可能正在被其他程序使用。",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SaveVideoRecordsAsync(List<SopVideoRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VideoIndexPath)!);
        await File.WriteAllTextAsync(VideoIndexPath,
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DeleteVideoFiles(SopVideoRecord record)
    {
        var dataDirectory = Path.GetFullPath(AppServices.Instance.Settings.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var videoPath = Path.GetFullPath(Path.Combine(
            AppServices.Instance.Settings.DataDirectory, record.RelativeVideoPath));
        if (!videoPath.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("视频路径不在数据目录内。");

        if (File.Exists(videoPath)) File.Delete(videoPath);

        var framesDirectory = Path.Combine(
            Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath));
        if (Directory.Exists(framesDirectory)) Directory.Delete(framesDirectory, recursive: true);
    }

    private void VideoPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source is null) return;

        if (_videoIsPlaying)
        {
            VideoPlayer.Pause();
            _videoIsPlaying = false;
            _videoProgressTimer.Stop();
        }
        else
        {
            VideoPlayer.Play();
            _videoIsPlaying = true;
            _videoProgressTimer.Start();
        }

        UpdateVideoPlayPauseButton();
    }

    private void UpdateVideoPlayPauseButton()
    {
        VideoPauseGlyph.Visibility = _videoIsPlaying ? Visibility.Visible : Visibility.Collapsed;
        VideoPlayGlyph.Visibility = _videoIsPlaying ? Visibility.Collapsed : Visibility.Visible;
        VideoPlayPauseButton.ToolTip = _videoIsPlaying ? "暂停" : "播放";
    }

    private void VideoStop_Click(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Stop();
        _videoProgressTimer.Stop();
        ResetVideoProgress();
    }

    private static VideoWriter? CreateWriter(string path, double fps, OpenCvSharp.Size size)
    {
        foreach (var codec in new[] { FourCC.MJPG, FourCC.XVID, FourCC.MP4V, FourCC.DIVX })
        {
            VideoWriter? candidate = null;
            try
            {
                candidate = new VideoWriter(path, codec, fps, size);
                if (candidate.IsOpened()) return candidate;
            }
            catch
            {
            }
            candidate?.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
        return null;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "sop" : clean;
    }
}
