using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Application;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

/// <summary>SOP 流程模型、步骤模型配置与版本管理。</summary>
public partial class SopPage : UserControl
{
    private sealed record DefinitionRow(SopDefinition Definition)
    {
        public string DisplayName => $"{Definition.Name}  ·  {Definition.Code}  ·  v{Definition.Version}  ·  {GetStatus(Definition.Status)}";
    }

    private sealed class StepEditorRow : INotifyPropertyChanged
    {
        private string _order = "1";
        private string _code = "STEP-01";
        private string _modelPath = "";
        public string Order
        {
            get => _order;
            set
            {
                if (string.Equals(_order, value, StringComparison.Ordinal)) return;
                _order = value;
                OnPropertyChanged();
            }
        }
        public string Code
        {
            get => _code;
            set
            {
                if (string.Equals(_code, value, StringComparison.Ordinal)) return;
                _code = value;
                OnPropertyChanged();
            }
        }
        public string ModelPath
        {
            get => _modelPath;
            set
            {
                if (string.Equals(_modelPath, value, StringComparison.Ordinal)) return;
                _modelPath = value;
                OnPropertyChanged();
            }
        }
        public string Name { get; set; } = "新步骤";
        private string _modelType = "目标检测";
        public string ModelType
        {
            get => _modelType;
            set
            {
                if (string.Equals(_modelType, value, StringComparison.Ordinal)) return;
                _modelType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelTypeDisplay));
            }
        }
        public string ModelTypeDisplay => GetTaskTypeDisplay(ModelType);
        public string ModelId { get; set; } = "";
        public string ModelVersion { get; set; } = "";
        public string PluginId { get; set; } = "";
        public string ExecutionProvider { get; set; } = "CPU";
        public string SettingsJson { get; set; } = "{}";
        public string ClassId { get; set; } = "class_id";
        public string MinConfidence { get; set; } = "0.60";
        public string StableFrames { get; set; } = "3";
        public string TimeoutSeconds { get; set; } = "30";
        public string RecordingSeconds { get; set; } = "5";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

    private readonly ObservableCollection<StepEditorRow> _steps = [];
    private IReadOnlyList<SopDefinition> _definitions = [];
    private SopDefinition? _selectedDefinition;
    private bool _loading;
    private IReadOnlySet<string> _availablePluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SopVideoRecord> _videoRecords = [];
    private readonly object _captureLock = new();
    private readonly PreviewRenderer _capturePreview = new();
    private ICameraSession? _captureSession;
    private VideoWriter? _captureWriter;
    private string? _captureVideoPath;
    private string? _captureFramesDirectory;
    private string? _captureRecordId;
    private int _captureFrameCount;
    private bool _captureRecording;
    private CancellationTokenSource? _captureScheduleCancellation;
    private int _captureGeneration;

    public SopPage()
    {
        InitializeComponent();
        StepsGrid.ItemsSource = _steps;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        await RefreshCaptureCamerasAsync();
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync(saveRecord: true);
        await StopCaptureCameraAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(_selectedDefinition?.Id);

    private async Task LoadAsync(string? selectId = null)
    {
        _loading = true;
        try
        {
            LoadAvailablePlugins();
            var catalog = (await AppServices.Instance.SopDefinitions.ListAsync()).ToList();

            // 兼容第一版直接写入任务的 SOP：第一次进入制作页时自动收编到独立目录。
            foreach (var inline in (await AppServices.Instance.Recipes.ListAsync())
                         .Select(item => item.Recipe.Sop?.Definition).OfType<SopDefinition>())
            {
                if (catalog.Any(item => string.Equals(item.Id, inline.Id, StringComparison.Ordinal)))
                {
                    continue;
                }
                catalog.Add(inline);
                await AppServices.Instance.SopDefinitions.SaveAsync(inline);
            }

            _definitions = catalog
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var definitionRows = _definitions.Select(item => new DefinitionRow(item)).ToArray();
            DefinitionList.ItemsSource = definitionRows;

            var selected = definitionRows.FirstOrDefault(item =>
                string.Equals(item.Definition.Id, selectId ?? _selectedDefinition?.Id, StringComparison.Ordinal));
            if (selected is not null)
            {
                DefinitionList.SelectedItem = selected;
                // LoadAsync suppresses SelectionChanged while rebuilding the list;
                // explicitly refresh the editor so binding status and step data are current.
                _selectedDefinition = selected.Definition;
                LoadDefinition(selected.Definition);
            }
            else if (definitionRows.Length > 0)
            {
                DefinitionList.SelectedIndex = 0;
            }
            else
            {
                ClearEditor();
                StatusText.Text = "暂无SOP定义，请点击“新建SOP”开始制作。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败：{ex.Message}";
            ThemedMessageBox.Show($"加载 SOP 定义失败：{ex.Message}", "SOP流程", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
        }
    }

    private void DefinitionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefinitionList.SelectedItem is not DefinitionRow row)
        {
            return;
        }

        _selectedDefinition = row.Definition;
        LoadDefinition(row.Definition);
    }

    private void LoadAvailablePlugins()
    {
        _availablePluginIds = AppServices.Instance.AlgorithmManager.ScanPlugins()
            .Where(plugin => plugin.Status == Contracts.Plugins.PluginStatus.Valid && plugin.Manifest is not null)
            .Select(plugin => plugin.Manifest!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _selectedDefinition = null;
        DefinitionList.SelectedItem = null;
        ClearEditor();
        EditorPanel.IsEnabled = true;
        DefinitionNameText.Focus();
        StatusText.Text = "正在制作新SOP草稿，填写步骤后点击“保存”。";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition is not { } definition)
        {
            ThemedMessageBox.Show("请先选择要复制的 SOP。", "复制SOP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 先完整加载源定义，再切换为未保存的新定义；这样步骤、模型参数和条件都会被带入。
        LoadDefinition(definition);
        _selectedDefinition = null;
        DefinitionList.SelectedItem = null;
        DefinitionNameText.Text = BuildCopyName(definition.Name);
        DefinitionCodeText.Text = BuildCopyCode();
        VersionText.Text = "1";
        EditorPanel.IsEnabled = true;
        DefinitionNameText.Focus();
        StatusText.Text = "已复制为新 SOP 草稿；修改完成后点击“保存”即可生成独立流程。";
    }

    private void Recording_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is Shell shell)
            shell.NavigateToSopRecordingPage();
    }

    private string BuildCopyName(string sourceName)
    {
        var baseName = $"{sourceName}（副本）";
        if (!_definitions.Any(item => string.Equals(item.Name, baseName, StringComparison.CurrentCultureIgnoreCase)))
        {
            return baseName;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!_definitions.Any(item => string.Equals(item.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
            {
                return candidate;
            }
        }
        return $"{baseName} {Guid.NewGuid():N}";
    }

    private static string BuildCopyCode() =>
        $"SOP-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        try
        {
            if (_selectedDefinition?.Status == SopDefinitionStatus.Published)
            {
                ThemedMessageBox.Show("已发布版本不能原位修改。请点击“新建SOP”制作新版本，避免影响已经投产的任务。",
                    "SOP版本保护", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var definition = BuildDefinition();
            await AppServices.Instance.SopDefinitions.SaveAsync(definition);
            _selectedDefinition = definition;
            StatusText.Text = $"已保存：{definition.Name} v{definition.Version}（{GetStatus(definition.Status)}）";
            await LoadAsync(definition.Id);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "保存SOP失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition is not { } definition)
        {
            ThemedMessageBox.Show("请先选择要删除的 SOP。", "SOP流程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tasks = await AppServices.Instance.Recipes.ListAsync();
        var boundTasks = tasks
            .Where(item => string.Equals(item.Recipe.Sop?.DefinitionId, definition.Id, StringComparison.Ordinal))
            .ToArray();

        if (boundTasks.Length > 0)
        {
            ThemedMessageBox.Show(
                $"SOP“{definition.Name}”已被 {boundTasks.Length} 个任务使用，请先到“任务配置”删除或改绑这些任务，再删除SOP。",
                "无法删除SOP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ThemedMessageBox.Show($"确认删除 SOP“{definition.Name}”吗？", "删除SOP",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await AppServices.Instance.SopDefinitions.DeleteAsync(definition.Id);
            _selectedDefinition = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"删除 SOP 失败：{ex.Message}", "SOP流程", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        var number = _steps.Count + 1;
        var row = new StepEditorRow
        {
            Order = number.ToString(CultureInfo.InvariantCulture),
            Code = $"STEP-{number:00}",
            Name = $"步骤{number}",
        };
        _steps.Add(row);
        StepsGrid.SelectedItem = row;
        StepsGrid.ScrollIntoView(row);
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = StepsGrid.SelectedIndex;
        if (StepsGrid.SelectedItem is StepEditorRow row)
        {
            _steps.Remove(row);
        }
        else if (_steps.Count > 0)
        {
            _steps.RemoveAt(_steps.Count - 1);
            selectedIndex = _steps.Count;
        }

        RenumberStepRows();
        if (_steps.Count > 0)
        {
            StepsGrid.SelectedIndex = Math.Clamp(selectedIndex, 0, _steps.Count - 1);
            StepsGrid.ScrollIntoView(StepsGrid.SelectedItem);
        }
    }

    private string CaptureIndexPath => Path.Combine(AppServices.Instance.Settings.ConfigDirectory, "sop-video-recordings.json");

    private string CaptureRootDirectory => Path.Combine(
        AppServices.Instance.Settings.DataDirectory,
        "sop-recordings",
        SafeFileName(_selectedDefinition?.Code ?? "draft"));

    private async Task RefreshCaptureCamerasAsync()
    {
        try
        {
            var previous = CaptureCameraCombo.SelectedItem as CameraDescriptor;
            var cameras = await AppServices.Instance.Cameras.DiscoverAllAsync(CancellationToken.None);
            CaptureCameraCombo.ItemsSource = cameras;
            CaptureCameraCombo.SelectedItem = cameras.FirstOrDefault(item =>
                previous is not null
                && string.Equals(item.ProviderId, previous.ProviderId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DeviceId, previous.DeviceId, StringComparison.OrdinalIgnoreCase))
                ?? cameras.FirstOrDefault();
            if (cameras.Count == 0)
            {
                CaptureVideoStatusText.Text = "未发现可用相机";
            }
        }
        catch (Exception ex)
        {
            CaptureVideoStatusText.Text = $"相机扫描失败：{ex.Message}";
        }
    }

    private async Task LoadVideoRecordsAsync(string definitionId)
    {
        List<SopVideoRecord> records = [];
        try
        {
            if (File.Exists(CaptureIndexPath))
            {
                var json = await File.ReadAllTextAsync(CaptureIndexPath);
                records = JsonSerializer.Deserialize<List<SopVideoRecord>>(json) ?? [];
            }
        }
        catch (Exception ex)
        {
            CaptureVideoStatusText.Text = $"录像列表加载失败：{ex.Message}";
        }

        _videoRecords.Clear();
        foreach (var record in records
                     .Where(item => string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal))
                     .OrderByDescending(item => item.StartedAt))
        {
            _videoRecords.Add(record);
        }
        CaptureVideoList.ItemsSource = _videoRecords;
        CaptureVideoStatusText.Text = _videoRecords.Count == 0
            ? "暂无已录制视频"
            : $"已有 {_videoRecords.Count} 个视频，选择后可以检查";
    }

    private bool TryGetCaptureSchedule(out List<(string Name, double Seconds)> schedule, out string message)
    {
        schedule = [];
        message = "";
        foreach (var row in _steps)
        {
            if (!double.TryParse(row.RecordingSeconds.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                message = $"步骤“{row.Name}”的录制时长必须大于 0 秒。";
                return false;
            }
            schedule.Add((row.Name.Trim(), seconds));
        }
        if (schedule.Count == 0)
        {
            message = "请先新增至少一个步骤。";
            return false;
        }
        return true;
    }

    private async void RefreshCaptureCameras_Click(object sender, RoutedEventArgs e)
        => await RefreshCaptureCamerasAsync();

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_captureRecording || _captureScheduleCancellation is not null)
        {
            await StopCaptureAsync(saveRecord: true);
            return;
        }
        if (_selectedDefinition is null)
        {
            ThemedMessageBox.Show("请先选择或新建一个 SOP。", "视频录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryGetCaptureSchedule(out var schedule, out var scheduleMessage))
        {
            ThemedMessageBox.Show(scheduleMessage, "视频录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CaptureCameraCombo.SelectedItem is not CameraDescriptor)
        {
            ThemedMessageBox.Show("请先选择相机。", "视频录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await StartCaptureCameraAsync();
            if (_captureSession is null) return;

            Directory.CreateDirectory(CaptureRootDirectory);
            _captureRecordId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            _captureVideoPath = Path.Combine(CaptureRootDirectory, $"{_captureRecordId}.avi");
            _captureFramesDirectory = Path.Combine(CaptureRootDirectory, _captureRecordId);
            _captureFrameCount = 0;
            _captureWriter = null;
            var generation = Interlocked.Increment(ref _captureGeneration);
            _captureScheduleCancellation = new CancellationTokenSource();
            CaptureButton.Content = "停止录制";
            CapturePreviewHintText.Visibility = Visibility.Collapsed;
            CaptureStepPromptText.Text = "准备开始";
            CaptureCountdownText.Text = "";
            _ = RunCaptureScheduleAsync(schedule, generation, _captureScheduleCancellation.Token);
        }
        catch (Exception ex)
        {
            CaptureButton.Content = "开始录制";
            CaptureVideoStatusText.Text = $"录制启动失败：{ex.Message}";
            await StopCaptureCameraAsync();
        }
    }

    private async Task StartCaptureCameraAsync()
    {
        if (_captureSession is not null) return;
        if (CaptureCameraCombo.SelectedItem is not CameraDescriptor descriptor)
            throw new InvalidOperationException("未选择相机");

        var options = AppServices.Instance.ApplyCameraDefaults(descriptor,
            new CameraOpenOptions { DesiredFps = 10 });
        var session = await AppServices.Instance.Cameras.OpenSessionAsync(
            descriptor, options, CancellationToken.None);
        try
        {
            session.FrameReceived += CaptureSession_FrameReceived;
            session.Faulted += CaptureSession_Faulted;
            await session.OpenAsync(options, CancellationToken.None);
            if (session.State == CameraSessionState.Faulted)
                throw new InvalidOperationException("相机打开失败，请检查设备是否被其他程序占用。");
            await session.StartAsync(CancellationToken.None);
            _captureSession = session;
            session = null!;
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
        }
    }

    private void CaptureSession_Faulted(object? sender, CameraFaultedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CaptureVideoStatusText.Text = $"相机故障：{e.Fault.Message}";
            if (_captureRecording) _ = StopCaptureAsync(saveRecord: true);
        });
    }

    private void CaptureSession_FrameReceived(object? sender, VideoFrameReceivedEventArgs e)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            _capturePreview.Render(CapturePreviewImage, e.Frame, maxFps: 15, force: true)));

        lock (_captureLock)
        {
            if (!_captureRecording || _captureVideoPath is null || _captureFramesDirectory is null) return;
            try
            {
                if (_captureWriter is null)
                {
                    Directory.CreateDirectory(_captureFramesDirectory);
                    _captureWriter = TryCreateCaptureWriter(_captureVideoPath, 10,
                        new OpenCvSharp.Size(e.Frame.Width, e.Frame.Height));
                    if (_captureWriter is null || !_captureWriter.IsOpened())
                        throw new InvalidOperationException("无法创建录像文件，请检查磁盘空间和视频编码器。");
                }
                using var mat = Mat.FromPixelData(e.Frame.Height, e.Frame.Width,
                    MatType.CV_8UC3, e.Frame.Pixels, e.Frame.Stride);
                _captureWriter.Write(mat);
                Cv2.ImWrite(Path.Combine(_captureFramesDirectory,
                    $"frame_{_captureFrameCount:000000}.jpg"), mat);
                _captureFrameCount++;
            }
            catch (Exception ex)
            {
                _captureRecording = false;
                Dispatcher.BeginInvoke(() => CaptureVideoStatusText.Text = $"录像保存失败：{ex.Message}");
            }
        }
    }

    private async Task RunCaptureScheduleAsync(List<(string Name, double Seconds)> schedule,
        int generation, CancellationToken cancellationToken)
    {
        try
        {
            var preparationSeconds = 3;
            if (int.TryParse(CapturePreparationSecondsText.Text.Trim(), out var configuredPreparation))
                preparationSeconds = Math.Clamp(configuredPreparation, 0, 30);
            for (var remaining = preparationSeconds; remaining > 0; remaining--)
            {
                SetCapturePrompt("准备开始，请保持画面稳定", $"{remaining} 秒后开始");
                await Task.Delay(1000, cancellationToken);
            }

            lock (_captureLock) _captureRecording = true;
            var startedAt = DateTimeOffset.Now;
            SetCapturePrompt($"请执行：{schedule[0].Name}", "录制中");
            for (var index = 0; index < schedule.Count; index++)
            {
                var step = schedule[index];
                var end = DateTimeOffset.UtcNow.AddSeconds(step.Seconds);
                while (DateTimeOffset.UtcNow < end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = Math.Max(0, (end - DateTimeOffset.UtcNow).TotalSeconds);
                    SetCapturePrompt($"请执行：{step.Name}", $"本步骤剩余 {remaining:0.0} 秒");
                    await Task.Delay(200, cancellationToken);
                }
            }
            SetCapturePrompt("录制完成", "正在保存视频…");
            if (generation == Volatile.Read(ref _captureGeneration))
                await Dispatcher.InvokeAsync(() => StopCaptureAsync(saveRecord: true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetCapturePrompt(string prompt, string countdown)
        => Dispatcher.BeginInvoke(() =>
        {
            CaptureStepPromptText.Text = prompt;
            CaptureCountdownText.Text = countdown;
        });

    private async Task StopCaptureAsync(bool saveRecord)
    {
        _captureScheduleCancellation?.Cancel();
        _captureScheduleCancellation?.Dispose();
        _captureScheduleCancellation = null;
        SopVideoRecord? record = null;
        lock (_captureLock)
        {
            _captureRecording = false;
            _captureWriter?.Release();
            _captureWriter?.Dispose();
            _captureWriter = null;
            if (saveRecord && _selectedDefinition is not null && _captureRecordId is not null
                && _captureVideoPath is not null && _captureFrameCount >= 2
                && File.Exists(_captureVideoPath))
            {
                var relativePath = Path.GetRelativePath(AppServices.Instance.Settings.DataDirectory,
                    _captureVideoPath);
                record = new SopVideoRecord
                {
                    Id = _captureRecordId,
                    DefinitionId = _selectedDefinition.Id,
                    DisplayName = $"{_captureRecordId}（{_captureFrameCount}帧）",
                    RelativeVideoPath = relativePath,
                    StartedAt = DateTimeOffset.Now,
                    DurationSeconds = _captureFrameCount / 10d,
                    FrameCount = _captureFrameCount,
                };
            }
        }

        _captureRecordId = null;
        _captureVideoPath = null;
        _captureFramesDirectory = null;
        CaptureButton.Content = "开始录制";
        CaptureCountdownText.Text = "";
        if (record is not null)
        {
            var allRecords = new List<SopVideoRecord>();
            if (File.Exists(CaptureIndexPath))
            {
                try
                {
                    allRecords = JsonSerializer.Deserialize<List<SopVideoRecord>>(
                        await File.ReadAllTextAsync(CaptureIndexPath)) ?? [];
                }
                catch { }
            }
            allRecords.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
            allRecords.Add(record);
            Directory.CreateDirectory(Path.GetDirectoryName(CaptureIndexPath)!);
            await File.WriteAllTextAsync(CaptureIndexPath,
                JsonSerializer.Serialize(allRecords, new JsonSerializerOptions { WriteIndented = true }));
            _videoRecords.Insert(0, record);
            CaptureVideoList.ItemsSource = _videoRecords;
            CaptureVideoStatusText.Text = $"已保存：{record.DisplayName}";
        }
        else if (saveRecord)
        {
            CaptureVideoStatusText.Text = "录像已停止，未生成有效视频";
        }
        await Task.CompletedTask;
    }

    private async Task StopCaptureCameraAsync()
    {
        var session = _captureSession;
        _captureSession = null;
        if (session is null) return;
        session.FrameReceived -= CaptureSession_FrameReceived;
        session.Faulted -= CaptureSession_Faulted;
        try { await session.StopAsync(CancellationToken.None); } catch { }
        try { await session.DisposeAsync(); } catch { }
    }

    private void CaptureVideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CaptureVideoPlayer.Stop();
        CaptureVideoPlayer.Close();
        if (CaptureVideoList.SelectedItem is not SopVideoRecord record) return;
        var path = Path.Combine(AppServices.Instance.Settings.DataDirectory, record.RelativeVideoPath);
        if (!File.Exists(path))
        {
            CaptureVideoStatusText.Text = "视频文件不存在，可能已被移动或删除";
            return;
        }
        CaptureVideoPlayer.Source = new Uri(path);
        CaptureVideoStatusText.Text = $"{record.DisplayName}，时长约 {record.DurationSeconds:0.0} 秒";
    }

    private void CaptureVideoPlay_Click(object sender, RoutedEventArgs e) => CaptureVideoPlayer.Play();
    private void CaptureVideoPause_Click(object sender, RoutedEventArgs e) => CaptureVideoPlayer.Pause();
    private void CaptureVideoStop_Click(object sender, RoutedEventArgs e) => CaptureVideoPlayer.Stop();

    private static VideoWriter? TryCreateCaptureWriter(string path, double fps, OpenCvSharp.Size size)
    {
        foreach (var codec in new[] { FourCC.MJPG, FourCC.XVID, FourCC.MP4V, FourCC.DIVX })
        {
            VideoWriter? candidate = null;
            try
            {
                candidate = new VideoWriter(path, codec, fps, size);
                if (candidate.IsOpened()) return candidate;
            }
            catch { }
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

    private void ChooseModelPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StepEditorRow row) return;
        var dialog = new OpenFileDialog
        {
            Title = $"选择{row.ModelType}模型",
            Filter = "模型文件|*.pt;*.pth;*.onnx;*.engine;*.bin;*.xml|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        var selectedPath = Path.GetFullPath(dialog.FileName);
        if (!ValidateModelPath(selectedPath, row.ModelType, out var validationMessage))
        {
            ThemedMessageBox.Show(validationMessage, "模型类型不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        row.ModelPath = selectedPath;
        StatusText.Text = $"已选择模型：{Path.GetFileName(selectedPath)}";
    }

    private void RenumberStepRows()
    {
        for (var index = 0; index < _steps.Count; index++)
        {
            var number = index + 1;
            _steps[index].Order = number.ToString(CultureInfo.InvariantCulture);
            _steps[index].Code = $"STEP-{number:00}";
        }
    }

    private void LoadDefinition(SopDefinition definition)
    {
        EditorPanel.IsEnabled = true;
        DefinitionNameText.Text = definition.Name;
        DefinitionCodeText.Text = definition.Code;
        ProductCodeText.Text = definition.ProductCode;
        VersionText.Text = definition.Version.ToString(CultureInfo.InvariantCulture);
        _steps.Clear();
        foreach (var step in definition.Steps.OrderBy(item => item.Order))
        {
            var condition = step.Conditions.FirstOrDefault();
            var execution = step.Execution ?? LegacyStepExecution(definition.Execution, step);
            _steps.Add(new StepEditorRow
            {
                Order = step.Order.ToString(CultureInfo.InvariantCulture),
                Code = step.Code,
                Name = step.Name,
                ModelType = GetTaskTypeDisplay(execution.TaskType.ToString()),
                ModelPath = string.IsNullOrWhiteSpace(execution.ModelPath)
                    ? ReadModelPath(execution.SettingsJson)
                    : execution.ModelPath,
                ModelId = execution.ModelId,
                ModelVersion = execution.ModelVersion,
                PluginId = execution.PluginId,
                ExecutionProvider = GetExecutionProviderDisplay(execution.ExecutionProvider),
                SettingsJson = string.IsNullOrWhiteSpace(execution.SettingsJson) ? "{}" : execution.SettingsJson,
                ClassId = condition?.ClassId ?? condition?.EventType ?? "class_id",
                MinConfidence = (condition?.MinConfidence ?? 0.6).ToString("0.##", CultureInfo.InvariantCulture),
                StableFrames = step.MinimumStableFrames.ToString(CultureInfo.InvariantCulture),
                TimeoutSeconds = step.TimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                RecordingSeconds = step.RecordingDurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
            });
        }
        _ = LoadVideoRecordsAsync(definition.Id);
        StatusText.Text = definition.Status == SopDefinitionStatus.Published
            ? "已发布版本只读保护：如需修改，请点击“新建SOP”制作新版本。"
            : $"当前编辑：{definition.Name} v{definition.Version}";
    }

    private void ClearEditor()
    {
        EditorPanel.IsEnabled = false;
        DefinitionNameText.Text = "";
        DefinitionCodeText.Text = "";
        ProductCodeText.Text = "";
        VersionText.Text = "";
        _steps.Clear();
        _videoRecords.Clear();
        CaptureVideoList.ItemsSource = _videoRecords;
        CaptureVideoPlayer.Stop();
        CaptureVideoPlayer.Close();
    }

    private static SopStepExecution LegacyStepExecution(SopExecutionProfile? profile, SopStep step)
    {
        if (profile is null)
        {
            return new SopStepExecution();
        }
        return new SopStepExecution
        {
            ModelPath = ReadModelPath(profile.SettingsJson),
            ModelId = $"legacy-{step.Code}",
            PluginId = profile.PluginId,
            TaskType = profile.TaskType,
            ExecutionProvider = profile.ExecutionProvider,
            SettingsJson = profile.SettingsJson,
            Roi = profile.Roi,
            RoiPolicy = profile.RoiPolicy,
            Rules = profile.Rules,
        };
    }

    private SopDefinition BuildDefinition()
    {
        var name = DefinitionNameText.Text.Trim();
        var code = DefinitionCodeText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("流程名称和流程编号不能为空。\n请先填写基本信息。");
        }
        if (!int.TryParse(VersionText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidOperationException("版本必须是大于等于 1 的整数。");
        }
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("SOP 至少需要配置一个工序步骤。");
        }

        var parsedSteps = new List<SopStep>(_steps.Count);
        foreach (var row in _steps)
        {
            if (!int.TryParse(row.Order.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) || order < 1)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的顺序必须是大于等于 1 的整数。");
            }
            if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.ClassId))
            {
                throw new InvalidOperationException("每个步骤都必须填写步骤编号、步骤名称和类别/匹配值。");
            }
            if (!TryParseTaskType(row.ModelType, out var taskType))
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的模型类型无效：{row.ModelType}。");
            }
            if (!TryParseExecutionProvider(row.ExecutionProvider, out var executionProvider))
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的推理设备只能填写 cpu 或 cuda。");
            }
            var settingsJson = string.IsNullOrWhiteSpace(row.SettingsJson) ? "{}" : row.SettingsJson.Trim();
            try
            {
                if (JsonDocument.Parse(settingsJson).RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"步骤“{row.Name}”的模型参数必须是 JSON 对象。");
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的模型参数不是合法 JSON：{ex.Message}");
            }
            if (!string.IsNullOrWhiteSpace(row.ModelPath))
            {
                var modelPath = Path.GetFullPath(row.ModelPath.Trim());
                if (!File.Exists(modelPath))
                {
                    throw new InvalidOperationException($"步骤“{row.Name}”选择的模型文件不存在：{modelPath}");
                }
                if (!ValidateModelPath(modelPath, row.ModelType, out var modelMessage))
                {
                    throw new InvalidOperationException($"步骤“{row.Name}”的模型路径无效：{modelMessage}");
                }
                settingsJson = ApplyModelPath(settingsJson, modelPath);
            }
            if (!double.TryParse(row.MinConfidence.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                || confidence is < 0 or > 1)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的置信度必须在 0 到 1 之间。");
            }
            if (!int.TryParse(row.StableFrames.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stableFrames) || stableFrames < 1)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的稳定帧数必须大于等于 1。");
            }
            if (!double.TryParse(row.TimeoutSeconds.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout) || timeout < 0)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的超时秒数必须是非负数。");
            }

            if (!double.TryParse(row.RecordingSeconds.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var recordingSeconds) || recordingSeconds <= 0)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的录制时长必须大于 0 秒。");
            }

            var stepId = $"{code}-step-{order:00}";
            parsedSteps.Add(new SopStep
            {
                Id = stepId,
                Order = order,
                Code = row.Code.Trim(),
                Name = row.Name.Trim(),
                Required = true,
                EnforceOrder = true,
                TimeoutSeconds = timeout,
                RecordingDurationSeconds = recordingSeconds,
                MinimumStableFrames = stableFrames,
                Execution = new SopStepExecution
                {
                    ModelPath = row.ModelPath.Trim(),
                    ModelId = string.IsNullOrWhiteSpace(row.ModelId) ? $"{code}-model-{order:00}" : row.ModelId.Trim(),
                    ModelVersion = row.ModelVersion.Trim(),
                    PluginId = ResolveAutomaticPluginId(taskType),
                    TaskType = taskType,
                    ExecutionProvider = executionProvider,
                    SettingsJson = settingsJson,
                    Rules = BuildStepRules(taskType, row.ClassId.Trim()),
                },
                Conditions =
                [
                    BuildCondition(stepId, taskType, row.ClassId.Trim(), confidence),
                ],
            });
        }

        if (parsedSteps.Select(step => step.Order).Distinct().Count() != parsedSteps.Count)
        {
            throw new InvalidOperationException("步骤顺序不能重复，请按 1、2、3……配置。");
        }

        return new SopDefinition
        {
            Id = _selectedDefinition?.Id ?? $"sop:{Guid.NewGuid():N}",
            Code = code,
            Name = name,
            ProductCode = ProductCodeText.Text.Trim(),
            Version = version,
            // SOP 不再暴露生命周期字段，保存即形成可绑定的正式版本。
            Status = SopDefinitionStatus.Published,
            // 输入源、设备/路径和运行模式属于任务实例，不属于可复用的 SOP 定义。
            // 保留旧版 Definition.Execution 的读取兼容性，但新 SOP 不再写入全局运行配置。
            Execution = null,
            Steps = parsedSteps.OrderBy(step => step.Order).ToArray(),
        };
    }

    private static SopCondition BuildCondition(string stepId, InspectionTaskType taskType, string value, double confidence)
    {
        var normalized = taskType.Normalize();
        return normalized switch
        {
            InspectionTaskType.AiText => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.TextEquals,
                EventType = "text.recognized", TextValue = value,
                MinConfidence = confidence, Required = true,
            },
            InspectionTaskType.Barcode or InspectionTaskType.QrCode => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.CodeEquals,
                EventType = "code.recognized", CodeValue = value,
                MinConfidence = confidence, Required = true,
            },
            InspectionTaskType.BehaviorRecognition => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.BehaviorCompleted,
                EventType = "behavior.completed", ClassId = value,
                MinConfidence = confidence, Required = true,
            },
            _ => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.ObjectPresent,
                EventType = "object.present", ClassId = value,
                MinConfidence = confidence, Required = true,
            },
        };
    }

    private static IReadOnlyList<InspectionRule> BuildStepRules(InspectionTaskType taskType, string value)
        => taskType.Normalize() is InspectionTaskType.Detection or InspectionTaskType.BehaviorRecognition
            ? [new InspectionRule
            {
                RuleId = "sop-step-check", Kind = RuleKind.CountAtLeast,
                ClassId = value, ExpectedCount = 1, MinConfidence = 0.1, Enabled = true,
            }]
            : [];

    /// <summary>
    /// 算法插件是运行时实现细节，不在 SOP 编辑器中暴露给用户。
    /// 根据工序模型类型选择宿主内置运行时；保留 PluginId 仅用于内部会话创建和历史兼容。
    /// </summary>
    private string ResolveAutomaticPluginId(InspectionTaskType taskType)
    {
        var normalized = taskType.Normalize();
        var preferred = normalized == InspectionTaskType.SemanticSegmentation
            ? new[] { "com.vision.atu5", "com.vision.yolo11" }
            : new[] { "com.vision.yolo11", "com.vision.atu5" };
        var pluginId = preferred.FirstOrDefault(id => _availablePluginIds.Contains(id));
        if (pluginId is null)
        {
            throw new InvalidOperationException($"步骤模型类型“{GetTaskTypeDisplay(taskType.ToString())}”没有可用的算法运行时，请检查安装目录中的算法组件。");
        }
        return pluginId;
    }

    private void CommitGridEdits()
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static string ReadModelPath(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return "";
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("modelPath", out var modelPath)
                && modelPath.ValueKind == JsonValueKind.String)
            {
                return modelPath.GetString()?.Trim() ?? "";
            }
        }
        catch (JsonException)
        {
            // BuildDefinition 会对设置 JSON 给出具体的格式错误提示。
        }
        return "";
    }

    private static string ApplyModelPath(string settingsJson, string modelPath)
    {
        var settings = JsonNode.Parse(settingsJson) as JsonObject ?? [];
        settings["modelPath"] = modelPath;
        return settings.ToJsonString();
    }

    private static bool ValidateModelPath(string modelPath, string modelType, out string message)
    {
        message = "";
        if (!File.Exists(modelPath))
        {
            message = $"模型文件不存在：{modelPath}";
            return false;
        }
        if (!TryParseTaskType(modelType, out var expectedType))
        {
            message = $"无法识别步骤模型类型：{modelType}。";
            return false;
        }
        if (!TryInferModelType(modelPath, out var actualType, out var evidence))
        {
            message = "无法从模型目录或文件名判断模型类型，请选择带有 model-node.json 的训练模型，或使用包含类型名称的模型路径。";
            return false;
        }
        if (actualType.Normalize() != expectedType.Normalize())
        {
            message = $"当前步骤类型为“{GetTaskTypeDisplay(expectedType.ToString())}”，但所选模型被识别为“{GetTaskTypeDisplay(actualType.ToString())}”（依据：{evidence}）。";
            return false;
        }
        return true;
    }

    private static bool TryInferModelType(string modelPath, out InspectionTaskType type, out string evidence)
    {
        type = (InspectionTaskType)(-1);
        evidence = "";
        var file = new FileInfo(modelPath);
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            var metadataPath = Path.Combine(directory.FullName, "model-node.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    var root = document.RootElement;
                    var taskTypeText = root.TryGetProperty("TaskType", out var taskType)
                        ? taskType.GetString()
                        : root.TryGetProperty("taskType", out var camelTaskType) ? camelTaskType.GetString() : null;
                    if (TryParseTaskType(taskTypeText, out type) || TryInferTypeFromText(taskTypeText, out type))
                    {
                        evidence = "model-node.json";
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // 继续使用路径特征判断。
                }
            }

            var argsPath = Path.Combine(directory.FullName, "args.yaml");
            if (File.Exists(argsPath) && TryInferTypeFromText(File.ReadAllText(argsPath), out type))
            {
                evidence = "args.yaml";
                return true;
            }
        }

        if (TryInferTypeFromText(modelPath, out type))
        {
            evidence = "模型路径";
            return true;
        }
        return false;
    }

    private static bool TryInferTypeFromText(string? value, out InspectionTaskType type)
    {
        type = (InspectionTaskType)(-1);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.ToLowerInvariant().Replace('_', '-').Replace('\\', '/');
        if (text.Contains("qrcode") || text.Contains("qr-code") || text.Contains("二维码"))
        {
            type = InspectionTaskType.QrCode;
        }
        else if (text.Contains("barcode") || text.Contains("bar-code") || text.Contains("条码"))
        {
            type = InspectionTaskType.Barcode;
        }
        else if (text.Contains("ocr") || text.Contains("character") || text.Contains("ai-text") || text.Contains("字符"))
        {
            type = InspectionTaskType.AiText;
        }
        else if (text.Contains("behavior") || text.Contains("pose") || text.Contains("行为") || text.Contains("姿态"))
        {
            type = InspectionTaskType.BehaviorRecognition;
        }
        else if (text.Contains("semantic") || text.Contains("语义"))
        {
            type = InspectionTaskType.SemanticSegmentation;
        }
        else if (text.Contains("instance") || text.Contains("segment") || text.Contains("实例分割"))
        {
            type = InspectionTaskType.InstanceSegmentation;
        }
        else if (text.Contains("detection") || text.Contains("detect") || text.Contains("yolo") || text.Contains("目标检测"))
        {
            type = InspectionTaskType.Detection;
        }
        return Enum.IsDefined(type);
    }

    private static string GetTaskTypeDisplay(string value)
        => TryParseTaskType(value, out var type)
            ? type.Normalize() switch
            {
                InspectionTaskType.Detection => "目标检测",
                InspectionTaskType.SemanticSegmentation => "语义分割",
                InspectionTaskType.InstanceSegmentation => "实例分割",
                InspectionTaskType.BehaviorRecognition => "行为识别",
                InspectionTaskType.AiText => "AI字符识别",
                InspectionTaskType.Barcode => "AI条码识别",
                InspectionTaskType.QrCode => "AI二维码识别",
                _ => value,
            }
            : value;

    private static bool TryParseTaskType(string? value, out InspectionTaskType type)
    {
        if (Enum.TryParse<InspectionTaskType>(value, true, out type) && Enum.IsDefined(type)) return true;
        type = value?.Trim() switch
        {
            "目标检测" => InspectionTaskType.Detection,
            "语义分割" => InspectionTaskType.SemanticSegmentation,
            "实例分割" => InspectionTaskType.InstanceSegmentation,
            "行为识别" => InspectionTaskType.BehaviorRecognition,
            "AI字符识别" => InspectionTaskType.AiText,
            "AI条码识别" => InspectionTaskType.Barcode,
            "AI二维码识别" => InspectionTaskType.QrCode,
            _ => (InspectionTaskType)(-1),
        };
        return Enum.IsDefined(type);
    }

    private static string GetExecutionProviderDisplay(string value)
        => value.Equals("cuda", StringComparison.OrdinalIgnoreCase) || value.Equals("CUDA", StringComparison.OrdinalIgnoreCase)
            ? "CUDA"
            : "CPU";

    private static bool TryParseExecutionProvider(string? value, out string provider)
    {
        provider = value?.Trim().ToLowerInvariant() switch
        {
            "cpu" or "CPU" => "cpu",
            "cuda" or "CUDA" or "cuda（gpu）" => "cuda",
            _ => "",
        };
        return provider is "cpu" or "cuda";
    }

    private static string GetStatus(SopDefinitionStatus status) => status switch
    {
        SopDefinitionStatus.Published => "已发布",
        SopDefinitionStatus.Retired => "已停用",
        _ => "草稿",
    };
}
