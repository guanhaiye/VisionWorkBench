using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCvSharp;
using VisionWorkbench.Application;

namespace VisionWorkbench.App;

public partial class BehaviorAnnotationPage : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private BehaviorDatasetDefinition? _dataset;
    private BehaviorSequenceSource? _activeSource;
    private List<string> _framePaths = [];
    private bool _changingSource;

    public BehaviorAnnotationPage() => InitializeComponent();

    private void NewDataset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择行为数据集目录" };
        if (dialog.ShowDialog() != true) return;
        var root = Path.GetFullPath(dialog.FolderName);
        _dataset = new BehaviorDatasetDefinition { Name = new DirectoryInfo(root).Name, RootDirectory = root };
        BehaviorDatasetStore.Save(_dataset);
        LoadDatasetIntoUi();
        StatusText.Text = "已新建行为数据集。建议导入视频；也可以导入已抽帧的图片序列。";
    }

    private void LoadDataset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含行为数据集文件的目录" };
        if (dialog.ShowDialog() != true) return;
        if (!BehaviorDatasetStore.TryLoadFromRoot(dialog.FolderName, out var dataset) || dataset is null)
        {
            ThemedMessageBox.Show("该目录没有 .visionworkbench/behavior-dataset.json。", "加载行为数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _dataset = dataset;
        LoadDatasetIntoUi();
        StatusText.Text = $"已加载 {_dataset.Name}，来源 {_dataset.Sources.Count} 个，行为片段 {_dataset.Clips.Count} 个。";
    }

    private void BrowseFrames_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择已按顺序编号的图片序列目录" };
        if (dialog.ShowDialog() != true) return;
        var selectedRoot = Path.GetFullPath(dialog.FolderName);
        if (_dataset is null)
        {
            _dataset = new BehaviorDatasetDefinition { Name = new DirectoryInfo(selectedRoot).Name, RootDirectory = selectedRoot };
        }
        var relative = Path.GetRelativePath(_dataset.RootDirectory, selectedRoot);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            ThemedMessageBox.Show("图片序列目录必须位于行为数据集目录内。请先用该目录新建数据集，或把图片复制到数据集目录。", "导入图片序列", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var normalized = BehaviorDatasetStore.NormalizeRelative(relative == "." ? "" : relative);
        var source = _dataset.Sources.FirstOrDefault(x => string.Equals(x.RelativeFramesDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                     ?? new BehaviorSequenceSource { Name = new DirectoryInfo(selectedRoot).Name, RelativeFramesDirectory = normalized, Kind = "frames" };
        if (!_dataset.Sources.Contains(source)) _dataset.Sources.Add(source);
        source.FrameCount = Directory.EnumerateFiles(selectedRoot).Count(x => ImageExtensions.Contains(Path.GetExtension(x)));
        source.FrameRate = 10;
        _activeSource = source;
        SaveDataset();
        RefreshSources();
        SourceCombo.SelectedItem = source;
        StatusText.Text = $"已导入图片序列 {_framePaths.Count} 帧。";
    }

    private void ClassesText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LabelCombo is null) return;
        var current = LabelCombo.Text;
        var classes = ClassesText.Text
            .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        LabelCombo.ItemsSource = classes;
        if (!string.IsNullOrWhiteSpace(current)) LabelCombo.Text = current;
    }

    private async void ImportVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
        {
            ThemedMessageBox.Show("请先新建或加载行为数据集。", "导入视频", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择行为视频",
            Filter = "视频文件|*.mp4;*.avi;*.mov;*.mkv;*.wmv|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        var sourceId = Guid.NewGuid().ToString("N");
        var source = new BehaviorSequenceSource
        {
            Id = sourceId,
            Name = Path.GetFileName(dialog.FileName),
            Kind = "video",
            RelativeVideoPath = BehaviorDatasetStore.NormalizeRelative(Path.Combine(".visionworkbench", "videos", $"{sourceId}{Path.GetExtension(dialog.FileName)}")),
            RelativeFramesDirectory = BehaviorDatasetStore.NormalizeRelative(Path.Combine(".visionworkbench", "rawframes", sourceId)),
        };
        var videoPath = Path.Combine(_dataset.RootDirectory, source.RelativeVideoPath);
        var framesDirectory = Path.Combine(_dataset.RootDirectory, source.RelativeFramesDirectory);
        try
        {
            StatusText.Text = "正在导入视频并抽取行为帧，请稍候……";
            Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
            Directory.CreateDirectory(framesDirectory);
            File.Copy(dialog.FileName, videoPath, true);
            var extraction = await Task.Run(() => ExtractVideoFrames(dialog.FileName, framesDirectory));
            source.FrameRate = extraction.Fps;
            source.FrameCount = extraction.FrameCount;
            source.Width = extraction.Width;
            source.Height = extraction.Height;
            _dataset.Sources.Add(source);
            _activeSource = source;
            SaveDataset();
            RefreshSources();
            SourceCombo.SelectedItem = source;
            StatusText.Text = $"视频导入完成：原始 {extraction.OriginalFrameCount} 帧，抽取 {extraction.FrameCount} 帧，时间轴按 {source.FrameRate:0.##} FPS。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "视频导入失败";
            ThemedMessageBox.Show(ex.Message, "导入视频失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static (double Fps, int FrameCount, int OriginalFrameCount, int Width, int Height) ExtractVideoFrames(string videoPath, string outputDirectory)
    {
        using var capture = new VideoCapture(videoPath);
        if (!capture.IsOpened()) throw new InvalidOperationException("无法打开视频文件。");
        var originalFps = capture.Fps > 0 ? capture.Fps : 25;
        var step = Math.Max(1, (int)Math.Round(originalFps / 10d));
        var saved = 0;
        var frameIndex = 0;
        using var frame = new Mat();
        while (capture.Read(frame))
        {
            if (frame.Empty()) break;
            if (frameIndex % step == 0)
            {
                var target = Path.Combine(outputDirectory, $"frame_{saved:000000}.jpg");
                if (!Cv2.ImWrite(target, frame)) throw new IOException($"无法写入视频帧：{target}");
                saved++;
            }
            frameIndex++;
        }
        if (saved < 2) throw new InvalidOperationException("视频有效帧数不足，至少需要 2 帧。");
        return (originalFps / step, saved, frameIndex, frame.Width, frame.Height);
    }

    private void SaveDataset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadDatasetFields(out var error))
        {
            ThemedMessageBox.Show(error, "保存行为数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SaveDataset();
        StatusText.Text = $"已保存：{_dataset!.Sources.Count} 个来源，{_dataset.Clips.Count} 个行为片段。";
    }

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || !Directory.Exists(_dataset.RootDirectory)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _dataset.RootDirectory, UseShellExecute = true });
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSource || SourceCombo.SelectedItem is not BehaviorSequenceSource source) return;
        _activeSource = source;
        RefreshFrames();
        StartFrameText.Text = "0";
        EndFrameText.Text = Math.Max(0, _framePaths.Count - 1).ToString();
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _framePaths.Count == 0) return;
        var index = Math.Clamp((int)Math.Round(TimelineSlider.Value), 0, _framePaths.Count - 1);
        CurrentFrameText.Text = $"{index + 1}/{_framePaths.Count}";
        PreviewImage.Source = LoadBitmap(Path.Combine(_dataset?.RootDirectory ?? "", _framePaths[index]));
    }

    private void FrameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FrameList.SelectedItem is not string relative || _framePaths.Count == 0) return;
        var index = _framePaths.IndexOf(relative);
        if (index >= 0) TimelineSlider.Value = index;
        StatusText.Text = $"已选择 {FrameList.SelectedItems.Count} 帧。可直接设置时间片段起止帧。";
    }

    private void SetStartFrame_Click(object sender, RoutedEventArgs e) => StartFrameText.Text = ((int)Math.Round(TimelineSlider.Value)).ToString();
    private void SetEndFrame_Click(object sender, RoutedEventArgs e) => EndFrameText.Text = ((int)Math.Round(TimelineSlider.Value)).ToString();

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is not BehaviorClipDefinition clip) return;
        LabelCombo.Text = clip.Label;
        ClipInfoText.Text = $"来源：{_dataset?.Sources.FirstOrDefault(x => x.Id == clip.SourceId)?.Name ?? "图片序列"}{Environment.NewLine}" +
                            $"帧范围：{clip.StartFrame} - {clip.EndFrame}{Environment.NewLine}帧数：{clip.Frames.Count}";
        if (_dataset?.Sources.FirstOrDefault(x => x.Id == clip.SourceId) is { } source)
        {
            _activeSource = source;
            SourceCombo.SelectedItem = source;
            StartFrameText.Text = clip.StartFrame.ToString();
            EndFrameText.Text = clip.EndFrame.ToString();
            if (_framePaths.Count > 0) TimelineSlider.Value = Math.Clamp(clip.StartFrame, 0, _framePaths.Count - 1);
        }
    }

    private void ClipList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        if (element is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void DeleteClipContextMenu_Click(object sender, RoutedEventArgs e) => DeleteClip_Click(sender, e);

    private void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _activeSource is null) return;
        if (!int.TryParse(StartFrameText.Text, out var start) || !int.TryParse(EndFrameText.Text, out var end))
        {
            ThemedMessageBox.Show("请输入有效的起止帧。", "时间片段", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        start = Math.Clamp(start, 0, Math.Max(0, _framePaths.Count - 1));
        end = Math.Clamp(end, start, Math.Max(0, _framePaths.Count - 1));
        var label = LabelCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(label) || end - start + 1 < 2)
        {
            ThemedMessageBox.Show("请设置至少 2 帧的时间片段，并填写行为类别。", "行为标注", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var selected = ClipList.SelectedItem as BehaviorClipDefinition;
        var existing = selected is not null
                       && string.Equals(selected.SourceId, _activeSource.Id, StringComparison.OrdinalIgnoreCase)
                       && selected.StartFrame == start
                       && selected.EndFrame == end
            ? selected
            : _dataset.Clips.FirstOrDefault(x =>
                string.Equals(x.SourceId, _activeSource.Id, StringComparison.OrdinalIgnoreCase)
                && x.StartFrame == start
                && x.EndFrame == end);
        var frames = _framePaths.Skip(start).Take(end - start + 1).ToList();
        if (existing is not null)
        {
            existing.Label = label; existing.SourceId = _activeSource.Id; existing.StartFrame = start; existing.EndFrame = end; existing.Frames = frames;
        }
        else
        {
            existing = new BehaviorClipDefinition { Label = label, SourceId = _activeSource.Id, StartFrame = start, EndFrame = end, Frames = frames };
            _dataset.Clips.Add(existing);
        }
        if (!_dataset.Classes.Contains(label, StringComparer.OrdinalIgnoreCase)) _dataset.Classes.Add(label);
        SaveDataset();
        RefreshClipList();
        ClipList.SelectedItem = existing;
        LabelCombo.ItemsSource = _dataset.Classes;
        StatusText.Text = $"已自动保存时间片段：{label}（帧 {start}-{end}）。";
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || ClipList.SelectedItem is not BehaviorClipDefinition clip) return;
        if (ThemedMessageBox.Show($"确认删除行为片段“{clip.Label}”？", "删除行为片段", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _dataset.Clips.Remove(clip);
        SaveDataset(); RefreshClipList(); ClipInfoText.Clear();
        StatusText.Text = "行为片段已删除。";
    }

    private void LoadDatasetIntoUi()
    {
        if (_dataset is null) return;
        _dataset.Sources ??= [];
        DatasetNameText.Text = _dataset.Name;
        DatasetRootText.Text = _dataset.RootDirectory;
        ClassesText.Text = string.Join(", ", _dataset.Classes);
        SequenceLengthText.Text = _dataset.SequenceLength.ToString();
        RefreshSources();
        RefreshClipList();
        LabelCombo.ItemsSource = _dataset.Classes;
    }

    private void RefreshSources()
    {
        if (_dataset is null) return;
        _changingSource = true;
        SourceCombo.ItemsSource = null;
        SourceCombo.ItemsSource = _dataset.Sources;
        _changingSource = false;
        if (_activeSource is null || !_dataset.Sources.Contains(_activeSource)) _activeSource = _dataset.Sources.FirstOrDefault();
        SourceCombo.SelectedItem = _activeSource;
        RefreshFrames();
    }

    private void RefreshFrames()
    {
        if (_dataset is null) return;
        IEnumerable<string> paths;
        if (_activeSource is not null && !string.IsNullOrWhiteSpace(_activeSource.RelativeFramesDirectory))
        {
            var directory = Path.Combine(_dataset.RootDirectory, _activeSource.RelativeFramesDirectory);
            paths = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly) : [];
        }
        else
        {
            paths = Directory.Exists(_dataset.RootDirectory)
                ? Directory.EnumerateFiles(_dataset.RootDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(path => !Path.GetRelativePath(_dataset.RootDirectory, path).StartsWith(".visionworkbench", StringComparison.OrdinalIgnoreCase))
                : [];
        }
        _framePaths = paths.Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => BehaviorDatasetStore.NormalizeRelative(Path.GetRelativePath(_dataset.RootDirectory, path))).ToList();
        FrameList.ItemsSource = _framePaths;
        TimelineSlider.Maximum = Math.Max(1, _framePaths.Count - 1);
        TimelineSlider.Value = 0;
        CurrentFrameText.Text = _framePaths.Count == 0 ? "0/0" : $"1/{_framePaths.Count}";
        if (_activeSource is not null) _activeSource.FrameCount = _framePaths.Count;
    }

    private void RefreshClipList() { ClipList.ItemsSource = null; ClipList.ItemsSource = _dataset?.Clips; }

    private bool TryReadDatasetFields(out string error)
    {
        error = "";
        if (_dataset is null) { error = "请先新建或加载行为数据集。"; return false; }
        if (string.IsNullOrWhiteSpace(DatasetNameText.Text)) { error = "数据集名称不能为空。"; return false; }
        if (!int.TryParse(SequenceLengthText.Text, out var length) || length < 8 || length > 300) { error = "序列长度应为 8 到 300。"; return false; }
        _dataset.Name = DatasetNameText.Text.Trim(); _dataset.SequenceLength = length;
        _dataset.Classes = ClassesText.Text.Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Concat(_dataset.Clips.Select(x => x.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LabelCombo.ItemsSource = _dataset.Classes;
        return true;
    }

    private void SaveDataset()
    {
        if (_dataset is null || !TryReadDatasetFields(out _)) return;
        BehaviorDatasetStore.Save(_dataset);
        RegisterInDatasetCatalog();
    }

    private void RegisterInDatasetCatalog()
    {
        if (_dataset is null) return;
        var existing = AppServices.Instance.Datasets.List().FirstOrDefault(item => string.Equals(item.RootDirectory, _dataset.RootDirectory, StringComparison.OrdinalIgnoreCase));
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

    private static BitmapImage? LoadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path); var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }
}
