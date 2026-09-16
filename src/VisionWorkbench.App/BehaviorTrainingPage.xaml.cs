using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionWorkbench.Application;
using Path = System.IO.Path;

namespace VisionWorkbench.App;

public partial class BehaviorTrainingPage : UserControl
{
    private readonly List<(int Epoch, double Train, double Val)> _losses = [];
    private BehaviorDatasetOption? _dataset;
    private CancellationTokenSource? _cancellation;

    public BehaviorTrainingPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDatasets();
        DrawLossCurve();
    }

    private void RefreshDatasets_Click(object sender, RoutedEventArgs e) => RefreshDatasets();

    private void RefreshDatasets()
    {
        var options = AppServices.Instance.Datasets.List()
            .Select(x => x.RootDirectory)
            .Where(Directory.Exists)
            .Select(root => BehaviorDatasetStore.TryLoadFromRoot(root, out var dataset) ? dataset : null)
            .Where(x => x is not null)
            .Select(x => new BehaviorDatasetOption(x!))
            .GroupBy(x => x.Dataset.RootDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        DatasetCombo.ItemsSource = options;
        if (options.Count > 0) DatasetCombo.SelectedIndex = 0;
        else
        {
            DatasetHintText.Text = "尚未发现行为数据集。请先打开行为标注平台新建数据集。";
            StartButton.IsEnabled = false;
        }
    }

    private void DatasetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _dataset = DatasetCombo.SelectedItem as BehaviorDatasetOption;
        if (_dataset is null) return;
        SequenceLengthText.Text = _dataset.Dataset.SequenceLength.ToString();
        DatasetHintText.Text = $"类别：{string.Join("、", _dataset.Dataset.Classes)}；来源：{_dataset.Dataset.Sources.Count} 个；片段：{_dataset.Dataset.Clips.Count}。训练/验证按视频或序列来源划分，避免相邻帧泄漏。模型保存到 .visionworkbench/behavior-models。";
        PoseModelText.Text = FindDefaultPoseModel();
        RefreshResumeModels();
        ResetChart();
        StartButton.IsEnabled = _dataset.Dataset.Clips.Count >= 2 && _dataset.Dataset.Classes.Count >= 2;
    }

    private void RefreshResumeModels()
    {
        var models = new List<BehaviorModelOption> { new("从基础 Pose 模型开始", "") };
        if (_dataset is not null && Directory.Exists(BehaviorDatasetStore.GetModelsDirectory(_dataset.Dataset.RootDirectory)))
        {
            models.AddRange(Directory.EnumerateFiles(BehaviorDatasetStore.GetModelsDirectory(_dataset.Dataset.RootDirectory), "*.pt", SearchOption.AllDirectories)
                .Select(path => new BehaviorModelOption($"继续训练：{Path.GetRelativePath(_dataset.Dataset.RootDirectory, path)}", path)));
        }
        ResumeModelCombo.ItemsSource = models;
        ResumeModelCombo.SelectedIndex = 0;
    }

    private void BrowsePoseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 YOLO11-Pose 模型", Filter = "PyTorch 模型 (*.pt)|*.pt", CheckFileExists = true };
        if (dialog.ShowDialog() == true) PoseModelText.Text = dialog.FileName;
    }

    private void OpenAnnotation_Click(object sender, RoutedEventArgs e)
    {
        var page = new BehaviorAnnotationPage();
        var frame = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Child = page,
        };
        var window = new Window
        {
            Title = "行为标注平台",
            Content = frame,
            Width = 1200,
            Height = 760,
            Style = (Style)FindResource("ThemedDialogWindow"),
            Owner = Window.GetWindow(this),
        };
        window.ShowDialog();
        RefreshDatasets();
    }

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || !Directory.Exists(_dataset.Dataset.RootDirectory)) return;
        Process.Start(new ProcessStartInfo { FileName = _dataset.Dataset.RootDirectory, UseShellExecute = true });
    }

    private async void StartTraining_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) return;
        if (_dataset.Dataset.Classes.Count < 2 || _dataset.Dataset.Clips.Count < 2)
        {
            ThemedMessageBox.Show("自定义行为训练至少需要 2 个类别和 2 个行为片段。", "行为训练", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(EpochsText.Text, out var epochs) || epochs <= 0 || !int.TryParse(SequenceLengthText.Text, out var sequenceLength) || sequenceLength < 8)
        {
            ThemedMessageBox.Show("学习次数必须大于 0，序列长度不能小于 8。", "行为训练", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var poseModel = PoseModelText.Text.Trim();
        if (!File.Exists(poseModel) && !Path.IsPathRooted(poseModel)) poseModel = Path.GetFullPath(poseModel);
        if (!File.Exists(poseModel))
        {
            ThemedMessageBox.Show("找不到 YOLO11-Pose 基础模型，请先选择 yolo11n-pose.pt。", "行为训练", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var batch = int.Parse(((ComboBoxItem)BatchCombo.SelectedItem).Tag.ToString()!);
        var device = ((ComboBoxItem)DeviceCombo.SelectedItem).Tag.ToString()!;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var outputDirectory = BehaviorDatasetStore.GetModelsDirectory(_dataset.Dataset.RootDirectory);
        var resume = (ResumeModelCombo.SelectedItem as BehaviorModelOption)?.Path;
        var runName = $"{Sanitize(_dataset.Dataset.Name)}-{stamp}";
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.Combine(outputDirectory, runName));
        var request = new BehaviorTrainingRequest(
            BehaviorDatasetStore.GetMetadataPath(_dataset.Dataset.RootDirectory), poseModel, epochs, batch, sequenceLength,
            device, outputDirectory, runName, resume);
        try
        {
            SetRunning(true);
            ResetChart();
            AddLog($"开始训练：{_dataset.Dataset.Name}，类别 {string.Join(", ", _dataset.Dataset.Classes)}");
            if (!string.IsNullOrWhiteSpace(resume)) AddLog($"将在已有模型基础上继续训练：{resume}");
            _cancellation = new CancellationTokenSource();
            var progress = new Progress<BehaviorTrainingProgress>(HandleProgress);
            var result = await AppServices.Instance.BehaviorTraining.RunAsync(request, progress, _cancellation.Token);
            OutputPathText.Text = $"训练完成：{result.ModelPath}\n类别文件：{result.ClassesPath}";
            StatusText.Text = "行为模型训练完成";
            RefreshResumeModels();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "训练已停止";
            AddLog("用户停止训练。");
        }
        catch (Exception ex)
        {
            StatusText.Text = "训练失败";
            AddLog($"训练失败：{ex.Message}");
            ThemedMessageBox.Show(ex.Message, "行为训练失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null; SetRunning(false);
        }
    }

    private async void StopTraining_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Instance.BehaviorTraining.IsRunning) return;
        await AppServices.Instance.BehaviorTraining.CancelAsync();
        _cancellation?.Cancel();
    }

    private void HandleProgress(BehaviorTrainingProgress progress)
    {
        if (progress.Event == "epoch")
        {
            var train = progress.TrainLoss ?? 0;
            var val = progress.ValLoss ?? train;
            _losses.Add((progress.Epoch, train, val));
            CurrentLossText.Text = $"Epoch {progress.Epoch}/{progress.TotalEpochs}  Loss {train:0.0000}  Acc {progress.Accuracy:0.0%}";
            DrawLossCurve();
            AddLog($"{progress.Message}  Loss={train:0.0000}, Val={val:0.0000}, Acc={progress.Accuracy:0.0%}");
        }
        else if (!string.IsNullOrWhiteSpace(progress.Message)) { AddLog(progress.Message); StatusText.Text = progress.Message; }
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running && _dataset?.Dataset.Clips.Count >= 2 && _dataset.Dataset.Classes.Count >= 2;
        StopButton.IsEnabled = running;
        DatasetCombo.IsEnabled = !running;
        BatchCombo.IsEnabled = !running; SequenceLengthText.IsEnabled = !running; PoseModelText.IsEnabled = !running; ResumeModelCombo.IsEnabled = !running;
    }

    private void ResetChart() { _losses.Clear(); CurrentLossText.Text = "暂无训练数据"; LogList.Items.Clear(); OutputPathText.Text = ""; DrawLossCurve(); }
    private void LossCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawLossCurve();
    private void DrawLossCurve()
    {
        if (LossCanvas is null) return;
        LossCanvas.Children.Clear();
        var width = Math.Max(1, LossCanvas.ActualWidth); var height = Math.Max(1, LossCanvas.ActualHeight);
        const double left = 45, top = 12, right = 12, bottom = 28;
        var plotWidth = Math.Max(1, width - left - right); var plotHeight = Math.Max(1, height - top - bottom);
        var axisBrush = ThemeBrush("BorderBrush", Colors.Gray);
        LossCanvas.Children.Add(new Line { X1 = left, Y1 = top, X2 = left, Y2 = height - bottom, Stroke = axisBrush });
        LossCanvas.Children.Add(new Line { X1 = left, Y1 = height - bottom, X2 = width - right, Y2 = height - bottom, Stroke = axisBrush });
        if (_losses.Count == 0) return;
        var max = Math.Max(0.001, _losses.SelectMany(x => new[] { x.Train, x.Val }).Max());
        max *= 1.15; var maxEpoch = Math.Max(1, _losses.Max(x => x.Epoch));
        foreach (var (values, brush) in new[] { (_losses.Select(x => x.Train).ToArray(), Brushes.DodgerBlue), (_losses.Select(x => x.Val).ToArray(), Brushes.OrangeRed) })
        {
            var line = new Polyline { Stroke = brush, StrokeThickness = 2 };
            for (var i = 0; i < values.Length; i++)
            {
                var epoch = _losses[i].Epoch;
                line.Points.Add(new Point(left + (epoch - 1) * plotWidth / Math.Max(1, maxEpoch - 1), top + (max - values[i]) * plotHeight / max));
            }
            LossCanvas.Children.Add(line);
        }
    }
    private void AddLog(string text) { LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {text}"); if (LogList.Items.Count > 300) LogList.Items.RemoveAt(0); LogList.ScrollIntoView(LogList.Items[^1]); }
    private static Brush ThemeBrush(string key, Color fallback) => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private static string FindDefaultPoseModel()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "workers", "yolo11", "models", "yolo11n-pose.pt");
            if (File.Exists(candidate)) return candidate;
        }
        return "models/yolo11n-pose.pt";
    }
    private static string Sanitize(string value) => string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    private sealed record BehaviorDatasetOption(BehaviorDatasetDefinition Dataset)
    {
        public string DisplayName => $"{Dataset.Name}（{Dataset.Clips.Count}片段）";
        public override string ToString() => DisplayName;
    }

    private sealed record BehaviorModelOption(string DisplayName, string Path)
    {
        public override string ToString() => DisplayName;
    }
}
