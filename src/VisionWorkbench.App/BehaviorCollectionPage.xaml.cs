using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCvSharp;
using VisionWorkbench.Application;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.App;

public partial class BehaviorCollectionPage : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private readonly PreviewRenderer _previewRenderer = new();
    private readonly object _recordLock = new();
    private readonly object _recordWriteLock = new();
    private readonly List<Task> _recordWrites = new();
    private BehaviorDatasetDefinition? _dataset;
    private ICameraSession? _session;
    private VideoWriter? _writer;
    private string? _recordVideoPath;
    private string? _recordFramesDirectory;
    private string? _recordSourceId;
    private int _recordedFrames;
    private bool _recording;
    private bool _videoEncodingFallback;
    private bool _loaded;
    private double _recordFps = 10;
    private int _recordingGeneration;
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private List<string> _playbackFrames = [];
    private int _playbackIndex;
    private bool _updatingPlaybackSlider;

    public BehaviorCollectionPage() => InitializeComponent();

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshCamerasAsync();
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e) => await StopPreviewAsync();

    private void NewDataset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择行为数据集目录" };
        if (dialog.ShowDialog() != true) return;
        var root = Path.GetFullPath(dialog.FolderName);
        _dataset = new BehaviorDatasetDefinition { Name = new DirectoryInfo(root).Name, RootDirectory = root };
        BehaviorDatasetStore.Save(_dataset);
        RegisterInDatasetCatalog();
        UpdateDatasetUi();
        StatusText.Text = "行为数据集已创建，可以开始录制或导入视频。";
    }

    private void LoadDataset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含行为数据集文件的目录" };
        if (dialog.ShowDialog() != true) return;
        if (!BehaviorDatasetStore.TryLoadFromRoot(dialog.FolderName, out var dataset) || dataset is null)
        {
            ThemedMessageBox.Show("目录中没有 .visionworkbench/behavior-dataset.json。", "加载行为数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _dataset = dataset;
        RegisterInDatasetCatalog();
        UpdateDatasetUi();
        StatusText.Text = $"已加载数据集：{dataset.Name}，已有 {dataset.Sources.Count} 个采集源。";
    }

    private async void RefreshCamera_Click(object sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private async Task RefreshCamerasAsync()
    {
        try
        {
            var cameras = await AppServices.Instance.Cameras.DiscoverAllAsync(CancellationToken.None);
            CameraCombo.ItemsSource = cameras;
            CameraStatusText.Text = cameras.Count == 0 ? "未发现可用相机" : $"发现 {cameras.Count} 个相机";
            if (cameras.Count > 0) CameraCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = $"枚举相机失败：{ex.Message}";
        }
    }

    private async void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || CameraCombo.SelectedItem is not CameraDescriptor) return;
        await StartPreviewAsync();
    }

    private async Task StartPreviewAsync()
    {
        await StopPreviewAsync();
        if (CameraCombo.SelectedItem is not CameraDescriptor descriptor) return;
        try
        {
            _session = await AppServices.Instance.Cameras.OpenSessionAsync(descriptor,
                new CameraOpenOptions { DesiredFps = SelectedFps() }, CancellationToken.None);
            _session.FrameReceived += Session_FrameReceived;
            _session.Faulted += Session_Faulted;
            await _session.OpenAsync(new CameraOpenOptions { DesiredFps = SelectedFps() }, CancellationToken.None);
            await _session.StartAsync(CancellationToken.None);
            RecordButton.IsEnabled = true;
            PreviewHintText.Visibility = Visibility.Collapsed;
            CameraStatusText.Text = $"预览中：{descriptor.DisplayName}";
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = $"相机预览失败：{ex.Message}";
            await StopPreviewAsync();
        }
    }

    private async void StopPreview_Click(object sender, RoutedEventArgs e) => await StopPreviewAsync();

    private async void StartPreview_Click(object sender, RoutedEventArgs e) => await StartPreviewAsync();

    private async Task StopPreviewAsync()
    {
        if (_recording) await StopRecordingAsync();
        var session = _session;
        _session = null;
        RecordButton.IsEnabled = CameraCombo.SelectedItem is CameraDescriptor;
        if (session is null) return;
        try { await session.StopAsync(CancellationToken.None); } catch { }
        try { await session.DisposeAsync(); } catch { }
        PreviewImage.Source = null;
        PreviewHintText.Visibility = Visibility.Visible;
        CameraStatusText.Text = "预览已停止";
    }

    private void Session_Faulted(object? sender, CameraFaultedEventArgs e) =>
        Dispatcher.BeginInvoke(() => CameraStatusText.Text = $"相机故障：{e.Fault.Message}");

    private void Session_FrameReceived(object? sender, VideoFrameReceivedEventArgs e)
    {
        if (QueueFrameForRecording(e.Frame)) return;
        Dispatcher.BeginInvoke(() => _previewRenderer.Render(PreviewImage, e.Frame, maxFps: 15, force: true));
        if (!_recording) return;
        lock (_recordLock)
        {
            try
            {
                try
                {
                    EnsureRecorder(e.Frame);
                }
                catch (Exception)
                {
                    // 视频编码器不可用时仍保留 JPG 帧，不能让整次采集失败。
                    _writer?.Dispose();
                    _writer = null;
                    if (_recordVideoPath is not null && File.Exists(_recordVideoPath)) File.Delete(_recordVideoPath);
                    _videoEncodingFallback = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        RecordStatusText.Text = "视频编码器不可用，已切换为帧序列采集";
                        StatusText.Text = "当前系统没有可用的视频编码器；采集仍会保存 JPG 帧。";
                    });
                }
                using var mat = Mat.FromPixelData(e.Frame.Height, e.Frame.Width, MatType.CV_8UC3, e.Frame.Pixels, e.Frame.Stride);
                _writer?.Write(mat);
                if (_recordFramesDirectory is not null)
                {
                    var framePath = Path.Combine(_recordFramesDirectory, $"frame_{_recordedFrames:000000}.jpg");
                    Cv2.ImWrite(framePath, mat);
                }
                _recordedFrames++;
            }
            catch (Exception ex)
            {
                _recording = false;
                Dispatcher.BeginInvoke(() => ThemedMessageBox.Show(ex.Message, "录制失败", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        Dispatcher.BeginInvoke(() =>
        {
            _previewRenderer.Render(PreviewImage, e.Frame, maxFps: 15);
            if (_recording) RecordStatusText.Text = $"录制中：{_recordedFrames} 帧";
        });
    }

    private bool QueueFrameForRecording(VideoFrame frame)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            _previewRenderer.Render(PreviewImage, frame, maxFps: 15, force: true);
            if (_recording) RecordStatusText.Text = $"录制中：{Volatile.Read(ref _recordedFrames)} 帧";
        }));

        lock (_recordLock)
        {
            if (!_recording) return true;
            var frameNumber = Interlocked.Increment(ref _recordedFrames);
            _recordWrites.Add(Task.Run(() => SaveRecordedFrame(frame, frameNumber)));
        }
        return true;
    }

    private void SaveRecordedFrame(VideoFrame frame, int frameNumber)
    {
        lock (_recordWriteLock)
        {
            try
            {
                try
                {
                    EnsureRecorder(frame);
                }
                catch (Exception)
                {
                    _writer?.Dispose();
                    _writer = null;
                    if (_recordVideoPath is not null && File.Exists(_recordVideoPath)) File.Delete(_recordVideoPath);
                    _videoEncodingFallback = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        RecordStatusText.Text = "视频编码器不可用，已切换为帧序列采集";
                        StatusText.Text = "当前系统没有可用的视频编码器；采集仍会保存 JPG 帧。";
                    });
                }

                using var mat = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC3, frame.Pixels, frame.Stride);
                _writer?.Write(mat);
                if (_recordFramesDirectory is not null)
                {
                    var framePath = Path.Combine(_recordFramesDirectory, $"frame_{frameNumber:000000}.jpg");
                    Cv2.ImWrite(framePath, mat);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => StatusText.Text = $"录制帧保存失败：{ex.Message}");
            }
        }
    }

    private void EnsureRecorder(VideoFrame frame)
    {
        if (_writer is not null) return;
        if (_recordVideoPath is null || _recordFramesDirectory is null) throw new InvalidOperationException("录制目录未准备好。");
        Directory.CreateDirectory(_recordFramesDirectory);
        _writer = TryCreateVideoWriter(_recordVideoPath, _recordFps, new OpenCvSharp.Size(frame.Width, frame.Height)) ?? throw new InvalidOperationException();
        if (!_writer.IsOpened()) throw new InvalidOperationException("无法创建录制视频文件，请检查磁盘空间或视频编码器。");
    }

    private static VideoWriter? TryCreateVideoWriter(string path, double fps, OpenCvSharp.Size size)
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
                // 编码器不可用时继续尝试其他编码器。
            }
            candidate?.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
        return null;
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            await StopRecordingAsync();
            return;
        }
        if (_dataset is null)
        {
            ThemedMessageBox.Show("请先新建或加载行为数据集。", "开始录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_session is null && CameraCombo.SelectedItem is CameraDescriptor)
            await StartPreviewAsync();
        if (_session is null)
        {
            ThemedMessageBox.Show("请先选择并打开相机。", "开始录制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sourceId = Guid.NewGuid().ToString("N");
        var videoRelative = BehaviorDatasetStore.NormalizeRelative(Path.Combine(BehaviorDatasetStore.InternalDirectoryName, "videos", $"capture_{sourceId}.avi"));
        var framesRelative = BehaviorDatasetStore.NormalizeRelative(Path.Combine(BehaviorDatasetStore.InternalDirectoryName, "rawframes", sourceId));
        _recordSourceId = sourceId;
        _recordVideoPath = Path.Combine(_dataset.RootDirectory, videoRelative);
        _recordFramesDirectory = Path.Combine(_dataset.RootDirectory, framesRelative);
        _recordFps = SelectedFps();
        _recordedFrames = 0;
        _videoEncodingFallback = false;
        _recording = true;
        var recordingGeneration = Interlocked.Increment(ref _recordingGeneration);
        RecordButton.Content = "停止录制";
        RecordStatusText.Text = "正在等待首帧...";
        StatusText.Text = "正在录制行为视频，完成后会自动登记为数据集采集源。";
        if (int.TryParse(DurationText.Text, out var seconds) && seconds > 0)
            _ = FinishAfterAsync(seconds, recordingGeneration);
    }

    private async Task FinishAfterAsync(int seconds, int recordingGeneration)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (_recording && recordingGeneration == Volatile.Read(ref _recordingGeneration))
            await Dispatcher.InvokeAsync(StopRecordingAsync);
    }

    private async Task StopRecordingAsync()
    {
        BehaviorSequenceSource? source = null;
        List<Task> pendingWrites;
        lock (_recordLock)
        {
            _recording = false;
            pendingWrites = new List<Task>(_recordWrites);
            _recordWrites.Clear();
        }
        try { await Task.WhenAll(pendingWrites); } catch { }
        lock (_recordWriteLock)
        {
            _writer?.Release();
            _writer?.Dispose();
            _writer = null;
            if (_dataset is not null && _recordSourceId is not null && _recordedFrames >= 2 && _recordVideoPath is not null && _recordFramesDirectory is not null)
            {
                source = new BehaviorSequenceSource
                {
                    Id = _recordSourceId,
                    Name = $"相机采集 {_recordSourceId[..8]}",
                    Kind = _videoEncodingFallback ? "frames" : "video",
                    RelativeVideoPath = _videoEncodingFallback ? "" : BehaviorDatasetStore.NormalizeRelative(Path.GetRelativePath(_dataset.RootDirectory, _recordVideoPath)),
                    RelativeFramesDirectory = BehaviorDatasetStore.NormalizeRelative(Path.GetRelativePath(_dataset.RootDirectory, _recordFramesDirectory)),
                    FrameRate = _recordFps,
                    FrameCount = _recordedFrames,
                };
                _dataset.Sources.Add(source);
                BehaviorDatasetStore.Save(_dataset);
            }
        }
        RecordButton.Content = "开始录制";
        RecordStatusText.Text = source is null ? "录制帧数不足，未登记数据源" : $"已保存 {_recordedFrames} 帧";
        StatusText.Text = source is null ? "录制未保存：至少需要 2 帧。" : $"采集完成：{source.Name}，已加入行为数据集。";
        RefreshSourceList();
        await Task.CompletedTask;
    }

    private async void ImportVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) { ShowDatasetRequired(); return; }
        var dialog = new OpenFileDialog { Title = "选择行为视频", Filter = "视频文件|*.mp4;*.avi;*.mov;*.mkv;*.wmv|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        var id = Guid.NewGuid().ToString("N");
        var videoRelative = BehaviorDatasetStore.NormalizeRelative(Path.Combine(BehaviorDatasetStore.InternalDirectoryName, "videos", $"import_{id}{Path.GetExtension(dialog.FileName)}"));
        var framesRelative = BehaviorDatasetStore.NormalizeRelative(Path.Combine(BehaviorDatasetStore.InternalDirectoryName, "rawframes", id));
        var videoPath = Path.Combine(_dataset.RootDirectory, videoRelative);
        var framesPath = Path.Combine(_dataset.RootDirectory, framesRelative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
            File.Copy(dialog.FileName, videoPath, true);
            var result = await Task.Run(() => ExtractVideoFrames(dialog.FileName, framesPath));
            _dataset.Sources.Add(new BehaviorSequenceSource { Id = id, Name = Path.GetFileName(dialog.FileName), Kind = "video", RelativeVideoPath = videoRelative, RelativeFramesDirectory = framesRelative, FrameRate = result.Fps, FrameCount = result.Count, Width = result.Width, Height = result.Height });
            BehaviorDatasetStore.Save(_dataset);
            RefreshSourceList();
            StatusText.Text = $"已导入视频并抽取 {result.Count} 帧。";
        }
        catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "导入视频失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ImportFrames_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) { ShowDatasetRequired(); return; }
        var dialog = new OpenFolderDialog { Title = "选择图片序列目录" };
        if (dialog.ShowDialog() != true) return;
        var id = Guid.NewGuid().ToString("N");
        var relative = BehaviorDatasetStore.NormalizeRelative(Path.Combine(BehaviorDatasetStore.InternalDirectoryName, "rawframes", id));
        var target = Path.Combine(_dataset.RootDirectory, relative);
        Directory.CreateDirectory(target);
        var files = Directory.EnumerateFiles(dialog.FolderName).Where(x => ImageExtensions.Contains(Path.GetExtension(x))).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < files.Length; i++) File.Copy(files[i], Path.Combine(target, $"frame_{i:000000}{Path.GetExtension(files[i]).ToLowerInvariant()}"), true);
        if (files.Length == 0) { StatusText.Text = "图片序列中没有可用图片。"; return; }
        var size = Cv2.ImRead(files[0]).Size();
        _dataset.Sources.Add(new BehaviorSequenceSource { Id = id, Name = new DirectoryInfo(dialog.FolderName).Name, Kind = "frames", RelativeFramesDirectory = relative, FrameRate = SelectedFps(), FrameCount = files.Length, Width = size.Width, Height = size.Height });
        BehaviorDatasetStore.Save(_dataset);
        RefreshSourceList();
        StatusText.Text = $"已导入图片序列：{files.Length} 帧。";
        await Task.CompletedTask;
    }

    private static (double Fps, int Count, int Width, int Height) ExtractVideoFrames(string path, string output)
    {
        Directory.CreateDirectory(output);
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened()) throw new InvalidOperationException("无法打开视频文件。");
        var originalFps = capture.Fps > 0 ? capture.Fps : 25;
        var step = Math.Max(1, (int)Math.Round(originalFps / 10d));
        var index = 0; var saved = 0; var width = 0; var height = 0;
        using var mat = new Mat();
        while (capture.Read(mat))
        {
            if (mat.Empty()) break;
            width = mat.Width; height = mat.Height;
            if (index % step == 0 && Cv2.ImWrite(Path.Combine(output, $"frame_{saved:000000}.jpg"), mat)) saved++;
            index++;
        }
        if (saved < 2) throw new InvalidOperationException("视频有效帧不足，至少需要 2 帧。");
        return (originalFps / step, saved, width, height);
    }

    private void UpdateDatasetUi()
    {
        if (_dataset is null) return;
        DatasetNameText.Text = _dataset.Name;
        DatasetRootText.Text = _dataset.RootDirectory;
        RefreshSourceList();
    }

    private void RegisterInDatasetCatalog()
    {
        if (_dataset is null) return;
        var existing = AppServices.Instance.Datasets.List()
            .FirstOrDefault(item => string.Equals(item.RootDirectory, _dataset.RootDirectory, StringComparison.OrdinalIgnoreCase));
        AppServices.Instance.Datasets.Save(new DatasetDefinition
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = _dataset.Name,
            RootDirectory = _dataset.RootDirectory,
            TaskType = "behavior",
            Classes = [.. _dataset.Classes],
            ImageSplits = existing?.ImageSplits ?? new Dictionary<string, string>(),
        });
    }

    private async void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await StopPlaybackAsync();
        if (SourceList.SelectedItem is not BehaviorSequenceSource source) return;

        await StopPreviewAsync();
        var directory = string.IsNullOrWhiteSpace(source.RelativeFramesDirectory)
            ? null
            : Path.Combine(_dataset?.RootDirectory ?? string.Empty, source.RelativeFramesDirectory);
        _playbackFrames = directory is not null && Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        _playbackIndex = 0;
        PlaybackSlider.IsEnabled = _playbackFrames.Count > 0;
        PlaybackSlider.Minimum = 0;
        PlaybackSlider.Maximum = Math.Max(0, _playbackFrames.Count - 1);
        PlaybackStatusText.Text = _playbackFrames.Count == 0
            ? "该来源没有可播放的帧文件"
            : $"{source.Name}：共 {_playbackFrames.Count} 帧，可播放或拖动进度条";
        PreviewHintText.Visibility = _playbackFrames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_playbackFrames.Count > 0) await RenderPlaybackFrameAsync(0, CancellationToken.None);
    }

    private async void PlaybackPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackFrames.Count == 0) return;
        if (_playbackTask is { IsCompleted: false }) return;
        if (_playbackIndex >= _playbackFrames.Count - 1) _playbackIndex = 0;
        _playbackCts = new CancellationTokenSource();
        _playbackTask = PlaybackLoopAsync(_playbackCts.Token);
        await Task.CompletedTask;
    }

    private async void PlaybackPause_Click(object sender, RoutedEventArgs e)
    {
        await StopPlaybackAsync();
        PlaybackStatusText.Text = $"已暂停：第 {_playbackIndex + 1}/{_playbackFrames.Count} 帧";
    }

    private async void PlaybackStop_Click(object sender, RoutedEventArgs e)
    {
        await StopPlaybackAsync();
        _playbackIndex = 0;
        if (_playbackFrames.Count > 0) await RenderPlaybackFrameAsync(0, CancellationToken.None);
        PlaybackStatusText.Text = _playbackFrames.Count == 0 ? "没有可播放的帧" : $"已停止：共 {_playbackFrames.Count} 帧";
    }

    private async void PlaybackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPlaybackSlider || _playbackFrames.Count == 0) return;
        _playbackIndex = Math.Clamp((int)Math.Round(e.NewValue), 0, _playbackFrames.Count - 1);
        await RenderPlaybackFrameAsync(_playbackIndex, CancellationToken.None);
    }

    private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _playbackIndex < _playbackFrames.Count)
            {
                await RenderPlaybackFrameAsync(_playbackIndex, cancellationToken);
                _playbackIndex++;
                var fps = SourceList.SelectedItem is BehaviorSequenceSource source && source.FrameRate > 0 ? source.FrameRate : 10;
                await Task.Delay(TimeSpan.FromSeconds(1d / fps), cancellationToken);
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                _playbackIndex = 0;
                PlaybackStatusText.Text = "播放完成";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StopPlaybackAsync()
    {
        _playbackCts?.Cancel();
        var task = _playbackTask;
        _playbackCts = null;
        _playbackTask = null;
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    private async Task RenderPlaybackFrameAsync(int index, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= _playbackFrames.Count) return;
        var path = _playbackFrames[index];
        var image = await Task.Run(() => LoadPlaybackImage(path), cancellationToken);
        if (image is null || cancellationToken.IsCancellationRequested) return;
        PreviewImage.Source = image;
        _updatingPlaybackSlider = true;
        PlaybackSlider.Value = index;
        _updatingPlaybackSlider = false;
        PlaybackStatusText.Text = $"第 {index + 1}/{_playbackFrames.Count} 帧";
    }

    private static BitmapImage? LoadPlaybackImage(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SourceList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void DeleteSource_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || SourceList.SelectedItem is not BehaviorSequenceSource source) return;
        var answer = ThemedMessageBox.Show(
            $"确定删除采集来源“{source.Name}”吗？\n对应的视频和帧文件也会从本地删除。",
            "删除采集来源", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await StopPlaybackAsync();
        var error = DeleteSourceFiles(source);
        _dataset.Sources.Remove(source);
        _dataset.Clips.RemoveAll(clip => string.Equals(clip.SourceId, source.Id, StringComparison.OrdinalIgnoreCase));
        BehaviorDatasetStore.Save(_dataset);
        ResetPlaybackView();
        RefreshSourceList();
        StatusText.Text = string.IsNullOrWhiteSpace(error)
            ? "已删除采集来源及其本地文件。"
            : $"采集来源已删除，但部分本地文件删除失败：{error}";
    }

    private async void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _dataset.Sources.Count == 0) return;
        var answer = ThemedMessageBox.Show(
            $"确定清空全部 {_dataset.Sources.Count} 个采集来源吗？\n对应的视频、帧文件和关联标注也会删除。",
            "清空采集来源", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await StopPlaybackAsync();
        var errors = _dataset.Sources.Select(DeleteSourceFiles)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .ToList();
        _dataset.Sources.Clear();
        _dataset.Clips.Clear();
        BehaviorDatasetStore.Save(_dataset);
        ResetPlaybackView();
        RefreshSourceList();
        StatusText.Text = errors.Count == 0
            ? "已清空全部采集来源及其本地文件。"
            : $"已清空采集来源，但有 {errors.Count} 项本地文件删除失败。";
    }

    private string DeleteSourceFiles(BehaviorSequenceSource source)
    {
        if (_dataset is null) return "未加载数据集";
        var root = Path.GetFullPath(_dataset.RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var errors = new List<string>();
        foreach (var relative in new[] { source.RelativeVideoPath, source.RelativeFramesDirectory })
        {
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var path = Path.GetFullPath(Path.Combine(_dataset.RootDirectory, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(relative);
                continue;
            }
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{relative}（{ex.Message}）");
            }
        }
        return string.Join("；", errors);
    }

    private void ResetPlaybackView()
    {
        _playbackFrames = [];
        _playbackIndex = 0;
        PlaybackSlider.IsEnabled = false;
        PlaybackSlider.Value = 0;
        PreviewImage.Source = null;
        PreviewHintText.Visibility = Visibility.Visible;
        PlaybackStatusText.Text = "选择已采集来源后可播放";
    }

    private void RefreshSourceList() => SourceList.ItemsSource = _dataset?.Sources.ToList() ?? [];

    private double SelectedFps() => double.TryParse((FpsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var fps) ? fps : 10;

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || !Directory.Exists(_dataset.RootDirectory)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _dataset.RootDirectory, UseShellExecute = true });
    }

    private void ShowDatasetRequired() => ThemedMessageBox.Show("请先新建或加载行为数据集。", "行为数据采集", MessageBoxButton.OK, MessageBoxImage.Information);
}
