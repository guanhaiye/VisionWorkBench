using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionWorkbench.Application;
using IoPath = System.IO.Path;

namespace VisionWorkbench.App;

public partial class TrainingPage : UserControl
{
    private readonly List<(int Epoch, double Loss)> _lossPoints = [];
    private DatasetDefinition? _dataset;
    private CancellationTokenSource? _trainingCancellation;

    public TrainingPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            RefreshDatasets();
            await RefreshBatchOptionsAsync();
        };
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && !AppServices.Instance.Yolo11Training.IsRunning)
            {
                RefreshDatasets();
                _ = RefreshBatchOptionsAsync();
            }
        };
        DrawLossCurve();
    }

    private void RefreshDatasets()
    {
        var datasets = AppServices.Instance.Datasets.List();
        DatasetCombo.ItemsSource = datasets;
        if (datasets.Count > 0) DatasetCombo.SelectedIndex = 0;
        else SetTaskTypeUi(null);
    }

    private void DatasetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _dataset = DatasetCombo.SelectedItem as DatasetDefinition;
        SetTaskTypeUi(_dataset);
        RefreshModels();
        ResetChart();
        _ = RefreshBatchOptionsAsync();
    }

    private void SetTaskTypeUi(DatasetDefinition? dataset)
    {
        var taskType = dataset?.TaskType;
        var taskName = taskType switch
        {
            "detection" => "目标检测（YOLO11 detect）",
            "instance_segmentation" => "实例分割（YOLO11-seg）",
            "semantic_segmentation" => "语义分割（YOLO11 不支持原生训练）",
            _ => "未选择数据集",
        };
        TaskTypeText.Text = $"任务：{taskName}";
        TaskTypeText.Foreground = taskType == "semantic_segmentation" ? Brushes.DarkRed : Brushes.DarkGreen;
        StartButton.IsEnabled = dataset is not null && taskType is ("detection" or "instance_segmentation");
        ModelHintText.Text = taskType == "semantic_segmentation"
            ? "YOLO11 官方支持目标检测和实例分割训练，不支持原生语义分割训练。当前数据集需要使用 YOLO26-sem 或其他语义分割训练方案。"
            : dataset is null
                ? "请选择一个已保存的数据集。"
                : "训练完成后，best.pt、last.pt 和训练曲线会保存到数据集目录下的 .visionworkbench/models。选择已有 .pt 模型后将以该模型为基础继续学习。";
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e) => RefreshModels();

    private void RefreshModels()
    {
        var options = new List<ModelOption>();
        var taskType = _dataset?.TaskType;
        if (taskType == "detection")
        {
            options.AddRange([
                new("使用预训练 YOLO11n（自动下载）", "models/yolo11n.pt", false),
                new("使用预训练 YOLO11s（自动下载）", "models/yolo11s.pt", false),
                new("使用预训练 YOLO11m（自动下载）", "models/yolo11m.pt", false),
            ]);
        }
        else if (taskType == "instance_segmentation")
        {
            options.AddRange([
                new("使用预训练 YOLO11n-seg（自动下载）", "models/yolo11n-seg.pt", false),
                new("使用预训练 YOLO11s-seg（自动下载）", "models/yolo11s-seg.pt", false),
                new("使用预训练 YOLO11m-seg（自动下载）", "models/yolo11m-seg.pt", false),
            ]);
        }

        if (_dataset is not null && Directory.Exists(_dataset.RootDirectory))
        {
            try
            {
                var files = Directory.EnumerateFiles(_dataset.RootDirectory, "*.pt", SearchOption.AllDirectories)
                    .Where(path => !path.Contains(IoPath.Combine(".visionworkbench", "training-data"), StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
                options.AddRange(files.Select(path => new ModelOption(
                    $"继续训练：{IoPath.GetRelativePath(_dataset.RootDirectory, path)}", path, true)));
            }
            catch (UnauthorizedAccessException)
            {
                // 数据集目录权限不足时仍保留预训练模型选项。
            }
        }

        ModelCombo.ItemsSource = options;
        if (options.Count > 0) ModelCombo.SelectedIndex = 0;
        var existingCount = options.Count(x => x.IsExisting);
        var baseHint = _dataset?.TaskType == "semantic_segmentation"
            ? "YOLO11 官方支持目标检测和实例分割训练，不支持原生语义分割训练。当前数据集需要使用 YOLO26-sem 或其他语义分割训练方案。"
            : _dataset is null
                ? "请选择一个已保存的数据集。"
                : "训练完成后，best.pt、last.pt 和训练曲线会保存到数据集目录下的 .visionworkbench/models。选择已有 .pt 模型后将以该模型为基础继续学习。";
        ModelHintText.Text = baseHint + (existingCount > 0
            ? $" 当前目录发现 {existingCount} 个可继续训练的模型。"
            : " 当前目录未发现已训练模型。");
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is ModelOption { IsExisting: true } option)
        {
            TrainingStatusText.Text = "已选择已有模型，开始训练时将继续学习。";
            TrainingStatusText.Foreground = Brushes.DarkBlue;
        }
        _ = RefreshBatchOptionsAsync();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = RefreshBatchOptionsAsync();

    private void ImageSizeText_LostFocus(object sender, RoutedEventArgs e) => _ = RefreshBatchOptionsAsync();

    private async Task RefreshBatchOptionsAsync()
    {
        if (BatchSizeCombo is null) return;
        var previous = BatchSizeCombo.SelectedItem is int value ? value : 2;
        var imageSize = int.TryParse(ImageSizeText.Text, out var parsedSize) && parsedSize > 0 ? parsedSize : 640;
        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        Yolo11GpuMemory gpu;
        try
        {
            gpu = await AppServices.Instance.Yolo11Training.QueryGpuMemoryAsync();
        }
        catch
        {
            gpu = new Yolo11GpuMemory(false, 0, 0);
        }
        var maxBatch = EstimateMaxBatch(gpu, device, imageSize);
        var options = new[] { 1, 2, 4, 8, 16, 32, 64 }.Where(batch => batch <= maxBatch).ToList();
        if (options.Count == 0) options.Add(1);
        BatchSizeCombo.ItemsSource = options;
        BatchSizeCombo.SelectedItem = options.Contains(previous) ? previous : options.Last();
        if (gpu.Available && device is ("auto" or "0"))
        {
            var freeGiB = gpu.FreeBytes / 1024d / 1024d / 1024d;
            BatchMemoryHintText.Text = $"当前可用显存约 {freeGiB:0.00} GB，估算安全最大批大小：{maxBatch}。仅提供 2 的幂次选项。";
        }
        else
        {
            BatchMemoryHintText.Text = "未检测到可用 CUDA 显存，当前按 CPU 安全上限提供批大小选项。";
        }
    }

    private int EstimateMaxBatch(Yolo11GpuMemory gpu, string device, int imageSize)
    {
        if (device == "cpu" || !gpu.Available) return 8;
        var freeGiB = gpu.FreeBytes / 1024d / 1024d / 1024d;
        var imageFactor = Math.Pow(imageSize / 640d, 2);
        var modelPath = (ModelCombo.SelectedItem as ModelOption)?.ModelPath ?? "yolo11n.pt";
        var modelFactor = modelPath.Contains("m", StringComparison.OrdinalIgnoreCase) ? 2.5
            : modelPath.Contains("s", StringComparison.OrdinalIgnoreCase) ? 1.6
            : 1.0;
        var estimatedPerBatchGiB = 0.8 * imageFactor * modelFactor;
        var rawMaximum = (int)Math.Floor(freeGiB * 0.65 / estimatedPerBatchGiB);
        var maximum = 1;
        while (maximum * 2 <= rawMaximum && maximum < 64) maximum *= 2;
        return maximum;
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择已训练的 YOLO11 模型",
            Filter = "PyTorch 模型 (*.pt)|*.pt",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        var options = ModelCombo.Items.OfType<ModelOption>().ToList();
        var option = new ModelOption($"手动加载：{dialog.FileName}", dialog.FileName, true);
        options.RemoveAll(x => string.Equals(x.ModelPath, option.ModelPath, StringComparison.OrdinalIgnoreCase));
        options.Add(option);
        ModelCombo.ItemsSource = options;
        ModelCombo.SelectedItem = option;
    }

    private async void StartTraining_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) return;
        if (_dataset.TaskType == "semantic_segmentation")
        {
            MessageBox.Show("YOLO11 不支持原生语义分割训练。请改用目标检测/实例分割数据集，或后续接入 YOLO26-sem。", "无法开始训练", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ModelCombo.SelectedItem is not ModelOption model)
        {
            MessageBox.Show("请先选择预训练模型或数据集目录中的已训练模型。", "训练", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryParsePositive(EpochsText.Text, "学习次数", out var epochs) ||
            !TryParsePositive(ImageSizeText.Text, "图像尺寸", out var imageSize)) return;
        if (BatchSizeCombo.SelectedItem is not int batchSize)
        {
            MessageBox.Show("请选择批大小。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var trainingDataDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "training-data", stamp);
        var outputDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
        var runName = $"{SanitizeFileName(_dataset.Name)}-{_dataset.TaskType}-{stamp}";
        try
        {
            SetTrainingState(true);
            ResetChart();
            TrainingStatusText.Text = "正在整理数据集……";
            AddLog($"任务：{TaskTypeText.Text}；模型：{model.DisplayName}");
            var dataYaml = await Task.Run(() => AppServices.Instance.Datasets.ExportYolo(_dataset, trainingDataDirectory));
            AddLog($"训练数据已生成：{dataYaml}");
            if (model.IsExisting) AddLog("将加载已有模型，并在该模型基础上继续学习。");

            _trainingCancellation = new CancellationTokenSource();
            var request = new Yolo11TrainingRequest(
                dataYaml,
                _dataset.TaskType,
                model.ModelPath,
                epochs,
                batchSize,
                imageSize,
                device,
                outputDirectory,
                runName,
                model.IsExisting);
            var progress = new Progress<Yolo11TrainingProgress>(HandleProgress);
            var result = await AppServices.Instance.Yolo11Training.RunAsync(request, progress, _trainingCancellation.Token);
            OutputPathText.Text = $"训练完成：{result.ModelPath}";
            TrainingStatusText.Text = "训练完成";
            AddLog("训练完成，模型和曲线已保存到数据集目录。");
            RefreshModels();
            ModelCombo.SelectedItem = ModelCombo.Items.OfType<ModelOption>()
                .FirstOrDefault(x => string.Equals(x.ModelPath, result.ModelPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            TrainingStatusText.Text = "训练已停止";
            AddLog("用户停止了训练。");
        }
        catch (Exception ex)
        {
            TrainingStatusText.Text = "训练失败";
            AddLog($"训练失败：{ex.Message}");
            MessageBox.Show(ex.Message, "YOLO11 训练失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _trainingCancellation?.Dispose();
            _trainingCancellation = null;
            SetTrainingState(false);
        }
    }

    private async void StopTraining_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Instance.Yolo11Training.IsRunning) return;
        await AppServices.Instance.Yolo11Training.CancelAsync();
        _trainingCancellation?.Cancel();
    }

    private void HandleProgress(Yolo11TrainingProgress progress)
    {
        if (progress.Event == "epoch")
        {
            if (progress.TrainLoss is { } loss)
            {
                _lossPoints.Add((progress.Epoch, loss));
                CurrentLossText.Text = $"Epoch {progress.Epoch}/{progress.TotalEpochs}  Loss: {loss:0.0000}";
                DrawLossCurve();
            }
            AddLog(progress.Message + (progress.TrainLoss is { } value ? $"，Loss={value:0.0000}" : ""));
        }
        else if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            AddLog(progress.Message);
            TrainingStatusText.Text = progress.Message;
        }
    }

    private void SetTrainingState(bool running)
    {
        StartButton.IsEnabled = !running && _dataset?.TaskType is ("detection" or "instance_segmentation");
        StopButton.IsEnabled = running;
        DatasetCombo.IsEnabled = !running;
        ModelCombo.IsEnabled = !running;
        BatchSizeCombo.IsEnabled = !running;
        ImageSizeText.IsEnabled = !running;
        DeviceCombo.IsEnabled = !running;
    }

    private void ResetChart()
    {
        _lossPoints.Clear();
        CurrentLossText.Text = "暂无训练数据";
        DrawLossCurve();
        TrainingLogList.Items.Clear();
        OutputPathText.Text = "";
    }

    private void LossCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawLossCurve();

    private void DrawLossCurve()
    {
        if (LossCanvas is null) return;
        LossCanvas.Children.Clear();
        var width = Math.Max(1, LossCanvas.ActualWidth);
        var height = Math.Max(1, LossCanvas.ActualHeight);
        const double left = 42;
        const double top = 12;
        const double right = 12;
        const double bottom = 28;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var axisBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));
        LossCanvas.Children.Add(new Line { X1 = left, Y1 = top, X2 = left, Y2 = height - bottom, Stroke = axisBrush });
        LossCanvas.Children.Add(new Line { X1 = left, Y1 = height - bottom, X2 = width - right, Y2 = height - bottom, Stroke = axisBrush });
        if (_lossPoints.Count == 0) return;

        // 根据当前数据动态收窄纵轴范围，避免 Loss 都挤在图表顶部。
        var dataMin = _lossPoints.Min(point => point.Loss);
        var dataMax = _lossPoints.Max(point => point.Loss);
        var dataRange = dataMax - dataMin;
        var padding = dataRange > 0.000001
            ? dataRange * 0.15
            : Math.Max(Math.Abs(dataMax) * 0.1, 0.1);
        var minLoss = Math.Max(0, dataMin - padding);
        var maxLoss = Math.Max(minLoss + 0.001, dataMax + padding);
        var maxEpoch = Math.Max(1, _lossPoints.Max(point => point.Epoch));

        var gridBrush = new SolidColorBrush(Color.FromRgb(232, 232, 232));
        const int gridCount = 4;
        for (var index = 0; index <= gridCount; index++)
        {
            var ratio = index / (double)gridCount;
            var y = top + ratio * plotHeight;
            LossCanvas.Children.Add(new Line
            {
                X1 = left,
                Y1 = y,
                X2 = width - right,
                Y2 = y,
                Stroke = gridBrush,
                StrokeDashArray = [2, 2],
            });
            var value = maxLoss - ratio * (maxLoss - minLoss);
            var label = new TextBlock
            {
                Text = value.ToString("0.###"),
                Foreground = Brushes.Gray,
                FontSize = 10,
                Width = left - 6,
                TextAlignment = TextAlignment.Right,
            };
            LossCanvas.Children.Add(label);
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Math.Max(0, y - 8));
        }

        var polyline = new Polyline { Stroke = Brushes.DodgerBlue, StrokeThickness = 2 };
        foreach (var point in _lossPoints)
        {
            var x = left + (point.Epoch - 1) * plotWidth / Math.Max(1, maxEpoch - 1);
            var y = top + (maxLoss - point.Loss) * plotHeight / (maxLoss - minLoss);
            polyline.Points.Add(new Point(x, y));
        }
        LossCanvas.Children.Add(polyline);
    }

    private void AddLog(string message)
    {
        TrainingLogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (TrainingLogList.Items.Count > 300) TrainingLogList.Items.RemoveAt(0);
        TrainingLogList.ScrollIntoView(TrainingLogList.Items[^1]);
    }

    private static bool TryParsePositive(string text, string name, out int value)
    {
        if (int.TryParse(text, out value) && value > 0) return true;
        MessageBox.Show($"{name}必须是大于 0 的整数。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = new string(IoPath.GetInvalidFileNameChars());
        return string.Join("_", value.Split(invalid.ToCharArray(), StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record ModelOption(string DisplayName, string ModelPath, bool IsExisting)
    {
        public override string ToString() => DisplayName;
    }
}
