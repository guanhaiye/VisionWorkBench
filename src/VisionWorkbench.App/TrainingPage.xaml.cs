using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VisionWorkbench.Application;
using IoPath = System.IO.Path;

namespace VisionWorkbench.App;

public partial class TrainingPage : UserControl
{
    private readonly List<(int Epoch, double Loss)> _lossPoints = [];
    private readonly List<string> _trainingLogs = [];
    private readonly string? _taskTypeFilter;
    private List<TrainingModelNode> _modelNodes = [];
    private int _batchOptionsRequestId;
    private DatasetDefinition? _dataset;
    private TrainingModelNode? _selectedModelNode;
    private CancellationTokenSource? _trainingCancellation;
    private bool _isNewModel = true;
    private bool _trainingPaused;
    private bool _updatingModelNodes;

    public TrainingPage(string? taskTypeFilter = null)
    {
        _taskTypeFilter = taskTypeFilter;
        InitializeComponent();
        SetBatchSizeOptions([1, 2, 4, 8], 2);
        Loaded += async (_, _) =>
        {
            RefreshDatasets();
            await RefreshBatchOptionsAsync();
        };
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && !AppServices.Instance.Yolo11Training.IsRunning && !AppServices.Instance.Atu5Training.IsRunning)
            {
                RefreshDatasets();
                _ = RefreshBatchOptionsAsync();
            }
        };
        DrawLossCurve();
    }

    private void RefreshDatasets()
    {
        var datasets = AppServices.Instance.Datasets.List()
            .Where(dataset => _taskTypeFilter is null || dataset.TaskType.Equals(_taskTypeFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DatasetCombo.ItemsSource = datasets;
        if (datasets.Count > 0) DatasetCombo.SelectedIndex = 0;
        else SetTaskTypeUi(null);
    }

    private void DatasetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _dataset = DatasetCombo.SelectedItem as DatasetDefinition;
        SetTaskTypeUi(_dataset);
        RefreshModels();
        RefreshModelNodes();
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
        TaskTypeText.Foreground = Brushes.DarkGreen;
        if (taskType == "semantic_segmentation") TaskTypeText.Text = "任务：语义分割（ATU5 / FPN）";
        StartButton.IsEnabled = dataset is not null && taskType is ("detection" or "instance_segmentation" or "semantic_segmentation");
        ModelHintText.Text = taskType == "semantic_segmentation"
            ? "YOLO11 官方支持目标检测和实例分割训练，不支持原生语义分割训练。当前数据集需要使用 YOLO26-sem 或其他语义分割训练方案。"
            : dataset is null
                ? "请选择一个已保存的数据集。"
                : "模型列表仅显示官方预训练模型。训练完成后，best.pt、last.pt 和训练曲线会保存到数据集目录下的 .visionworkbench/models。";
    }

    private void RefreshModels()
    {
        var options = new List<ModelOption>();
        var taskType = _dataset?.TaskType;
        if (taskType == "detection")
        {
            options.AddRange([
                OfficialModel("YOLO11n", "models/yolo11n.pt"),
                OfficialModel("YOLO11s", "models/yolo11s.pt"),
                OfficialModel("YOLO11m", "models/yolo11m.pt"),
                OfficialModel("YOLO11l", "models/yolo11l.pt"),
                OfficialModel("YOLO11x", "models/yolo11x.pt"),
            ]);
        }
        else if (taskType == "instance_segmentation")
        {
            options.AddRange([
                OfficialModel("YOLO11n-seg", "models/yolo11n-seg.pt"),
                OfficialModel("YOLO11s-seg", "models/yolo11s-seg.pt"),
                OfficialModel("YOLO11m-seg", "models/yolo11m-seg.pt"),
                OfficialModel("YOLO11l-seg", "models/yolo11l-seg.pt"),
                OfficialModel("YOLO11x-seg", "models/yolo11x-seg.pt"),
            ]);
        }

        else if (taskType == "semantic_segmentation")
        {
            var encoderPath = "models/atu5.pt";
            options.Add(OfficialModel("ATU5 ResNet50 编码器预训练", encoderPath));
        }

        ModelCombo.ItemsSource = options;
        if (options.Count > 0) ModelCombo.SelectedIndex = 0;
        var baseHint = _dataset?.TaskType == "semantic_segmentation"
            ? "YOLO11 官方支持目标检测和实例分割训练，不支持原生语义分割训练。当前数据集需要使用 YOLO26-sem 或其他语义分割训练方案。"
            : _dataset is null
                ? "请选择一个已保存的数据集。"
                : "模型列表仅显示官方预训练模型。训练完成后，best.pt、last.pt 和训练曲线会保存到数据集目录下的 .visionworkbench/models。";
        ModelHintText.Text = baseHint;
        if (_dataset?.TaskType == "semantic_segmentation")
            ModelHintText.Text = "ATU5 使用 atu5.pt 作为初始权重，初始化 ResNet50 编码器后训练 FPN 语义分割模型。训练输出保存到数据集目录下的 .visionworkbench/models。";
        if (_dataset?.TaskType == "semantic_segmentation")
            ModelHintText.Text = "ATU5 使用 atu5.pt 作为初始权重，初始化 ResNet50 编码器后训练 FPN 语义分割模型。训练输出保存到数据集目录下的 .visionworkbench/models。";
    }

    private static ModelOption OfficialModel(string name, string modelPath)
    {
        var available = string.Equals(modelPath, "models/atu5.pt", StringComparison.OrdinalIgnoreCase)
            ? AppServices.Instance.Atu5Training.IsModelAvailable(modelPath)
            : AppServices.Instance.Yolo11Training.IsModelAvailable(modelPath);
        var status = available
            ? "本地已存在"
            : "首次使用时下载";
        return new ModelOption($"官方 {name}（{status}）", modelPath, false);
    }

    private void RefreshModelNodes(string? preferredModelPath = null)
    {
        _modelNodes = [];
        if (_dataset is not null)
        {
            var modelDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
            if (Directory.Exists(modelDirectory))
            {
                foreach (var runDirectory in Directory.EnumerateDirectories(modelDirectory)
                             .OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    var node = LoadModelNode(runDirectory);
                    if (node is not null && string.Equals(node.TaskType, _dataset.TaskType, StringComparison.OrdinalIgnoreCase))
                        _modelNodes.Add(node);
                }
            }
        }

        _updatingModelNodes = true;
        ModelNodeList.ItemsSource = _modelNodes;
        var selected = _modelNodes.FirstOrDefault(node =>
            !string.IsNullOrWhiteSpace(preferredModelPath) &&
            string.Equals(node.ModelPath, preferredModelPath, StringComparison.OrdinalIgnoreCase))
            ?? _modelNodes.FirstOrDefault();
        ModelNodeList.SelectedItem = selected;
        _updatingModelNodes = false;

        if (selected is null) BeginNewModel();
        else LoadModelNodeIntoEditor(selected);
    }

    private TrainingModelNode? LoadModelNode(string runDirectory)
    {
        var metadataPath = IoPath.Combine(runDirectory, "model-node.json");
        try
        {
            if (File.Exists(metadataPath))
            {
            var node = JsonSerializer.Deserialize<TrainingModelNode>(File.ReadAllText(metadataPath));
                if (node is not null && !string.IsNullOrWhiteSpace(node.ModelPath))
                {
                    NormalizeLossPoints(node);
                    return node;
                }
            }

            var modelPath = Directory.EnumerateFiles(runDirectory, "best.pt", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(runDirectory, "last.pt", SearchOption.AllDirectories).FirstOrDefault();
            if (modelPath is null) return null;
            var nodeFromLegacyRun = new TrainingModelNode
            {
                Name = IoPath.GetFileName(runDirectory),
                DatasetRoot = _dataset?.RootDirectory ?? "",
                TaskType = _dataset?.TaskType ?? "",
                BaseModel = ReadYamlValue(IoPath.Combine(runDirectory, "args.yaml"), "model") ?? "",
                ModelPath = modelPath,
                RunDirectory = runDirectory,
                CompletedAt = Directory.GetLastWriteTime(runDirectory),
                Epochs = ReadYamlInt(IoPath.Combine(runDirectory, "args.yaml"), "epochs") ?? 0,
                BatchSize = ReadYamlInt(IoPath.Combine(runDirectory, "args.yaml"), "batch") ?? 0,
                ImageSize = ReadYamlInt(IoPath.Combine(runDirectory, "args.yaml"), "imgsz") ?? 0,
                Device = ReadYamlValue(IoPath.Combine(runDirectory, "args.yaml"), "device") ?? "",
                LossPoints = ReadLossPoints(IoPath.Combine(runDirectory, "results.csv")),
            };
            return nodeFromLegacyRun;
        }
        catch
        {
            return null;
        }
    }

    private void NewModel_Click(object sender, RoutedEventArgs e) => BeginNewModel();

    private void ModelNodeList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);

        if (element is ListBoxItem item && ModelNodeList.ItemContainerGenerator.ItemFromContainer(item) is TrainingModelNode node)
        {
            ModelNodeList.SelectedItem = node;
            item.Focus();
        }
    }

    private TrainingModelNode? GetSelectedModelNode() => ModelNodeList.SelectedItem as TrainingModelNode;

    private void OpenModelDirectoryMenu_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedModelNode();
        if (node is null) return;

        var directory = !string.IsNullOrWhiteSpace(node.RunDirectory) && Directory.Exists(node.RunDirectory)
            ? node.RunDirectory
            : IoPath.GetDirectoryName(node.ModelPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ThemedMessageBox.Show("模型本地目录不存在。", "打开模型目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private void OpenModelWeightsMenu_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedModelNode();
        if (node is null || string.IsNullOrWhiteSpace(node.ModelPath)) return;
        if (!File.Exists(node.ModelPath))
        {
            ThemedMessageBox.Show("模型权重文件不存在。", "打开权重文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = node.ModelPath,
            UseShellExecute = true,
        });
    }

    private void CopyModelPathMenu_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedModelNode();
        if (node is null || string.IsNullOrWhiteSpace(node.ModelPath)) return;
        Clipboard.SetText(node.ModelPath);
        TrainingStatusText.Text = "模型路径已复制到剪贴板。";
    }

    private void DeleteModelNodeMenu_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedModelNode();
        if (node is null || string.IsNullOrWhiteSpace(node.RunDirectory)) return;

        var runDirectory = IoPath.GetFullPath(node.RunDirectory);
        var modelRoot = _dataset is null
            ? ""
            : IoPath.GetFullPath(IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models"));
        var modelRootPrefix = modelRoot.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar)
                              + IoPath.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(modelRoot) || !runDirectory.StartsWith(modelRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            ThemedMessageBox.Show("为避免误删其他目录，当前模型目录不在本数据集的模型目录下。", "删除模型节点", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = ThemedMessageBox.Show(
            $"确定删除模型节点“{node.Name}”吗？\n\n此操作会删除该训练目录、本地权重、训练曲线和日志。",
            "删除模型节点",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        if (!TryChooseModelDeleteMode(node, out var deleteMode)) return;

        try
        {
            if (Directory.Exists(runDirectory))
            {
                if (deleteMode == ModelDeleteMode.RecycleBin)
                    VbFileSystem.DeleteDirectory(runDirectory,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Directory.Delete(runDirectory, recursive: true);
            }
            RefreshModelNodes();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"删除模型节点失败：{ex.Message}", "删除模型节点", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private enum ModelDeleteMode
    {
        RecycleBin,
        Permanent,
    }

    private bool TryChooseModelDeleteMode(TrainingModelNode node, out ModelDeleteMode mode)
    {
        mode = ModelDeleteMode.RecycleBin;
        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = "选择删除方式",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
        };
        if (System.Windows.Application.Current.TryFindResource("ThemedDialogWindow") is Style style)
            window.Style = style;

        var result = false;
        var selectedMode = ModelDeleteMode.RecycleBin;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "请选择模型节点的删除方式",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"模型节点：{node.Name}\n\n移动到回收站后可以恢复；彻底删除后无法恢复。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("MutedTextBrush"),
            Margin = new Thickness(0, 0, 0, 18),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "取消", MinWidth = 86, IsCancel = true };
        cancel.Style = (Style)System.Windows.Application.Current.FindResource("SecondaryButton");
        cancel.Click += (_, _) => window.Close();
        var recycle = new Button { Content = "移动到回收站", MinWidth = 120, Margin = new Thickness(8, 2, 0, 2) };
        recycle.Click += (_, _) => { selectedMode = ModelDeleteMode.RecycleBin; result = true; window.Close(); };
        var permanent = new Button
        {
            Content = "彻底删除",
            MinWidth = 100,
            Margin = new Thickness(8, 2, 0, 2),
            Background = (Brush)System.Windows.Application.Current.FindResource("DangerBrush"),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("DangerBrush"),
        };
        permanent.Click += (_, _) => { selectedMode = ModelDeleteMode.Permanent; result = true; window.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(recycle);
        buttons.Children.Add(permanent);
        panel.Children.Add(buttons);
        window.Content = new Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("SurfaceBrush"),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel,
        };
        window.ShowDialog();
        mode = selectedMode;
        return result;
    }

    private void ModelNodeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingModelNodes || ModelNodeList.SelectedItem is not TrainingModelNode node) return;
        LoadModelNodeIntoEditor(node);
    }

    private void BeginNewModel()
    {
        _selectedModelNode = null;
        _isNewModel = true;
        _updatingModelNodes = true;
        ModelNodeList.SelectedIndex = -1;
        _updatingModelNodes = false;
        EpochsText.Text = "100";
        ImageSizeText.Text = "640";
        SetBatchSizeOptions([1, 2, 4, 8], 2);
        DeviceCombo.SelectedIndex = 0;
        ExportTensorRtCheckBox.IsChecked = true;
        if (ModelCombo.Items.Count > 0) ModelCombo.SelectedIndex = 0;
        ResetChart();
        OutputPathText.Text = "";
        TrainingStatusText.Text = "已新建模型，可设置训练超参数。";
        SetModelEditorState(true);
        SetTrainingState(false);
    }

    private void LoadModelNodeIntoEditor(TrainingModelNode node)
    {
        _selectedModelNode = node;
        _isNewModel = false;
        EpochsText.Text = node.Epochs > 0 ? node.Epochs.ToString() : "—";
        ImageSizeText.Text = node.ImageSize > 0 ? node.ImageSize.ToString() : "—";
        if (node.BatchSize > 0) SetBatchSizeOptions([node.BatchSize], node.BatchSize);
        if (!string.IsNullOrWhiteSpace(node.Device))
        {
            DeviceCombo.SelectedItem = DeviceCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, node.Device, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(node.BaseModel))
        {
            var options = ModelCombo.Items.OfType<ModelOption>().ToList();
            if (!options.Any(option => string.Equals(option.ModelPath, node.BaseModel, StringComparison.OrdinalIgnoreCase)))
                options.Add(new ModelOption($"训练使用：{IoPath.GetFileName(node.BaseModel)}", node.BaseModel, true));
            ModelCombo.ItemsSource = options;
            ModelCombo.SelectedItem = options.FirstOrDefault(option =>
                string.Equals(option.ModelPath, node.BaseModel, StringComparison.OrdinalIgnoreCase));
        }
        _lossPoints.Clear();
        _lossPoints.AddRange(node.LossPoints.Select(point => (point.Epoch, point.Loss)));
        TrainingLogList.Items.Clear();
        _trainingLogs.Clear();
        foreach (var log in node.Logs)
        {
            _trainingLogs.Add(log);
            TrainingLogList.Items.Add(log);
        }
        CurrentLossText.Text = _lossPoints.Count > 0
            ? $"已加载 {_lossPoints.Count} 个训练点"
            : "暂无训练数据";
        OutputPathText.Text = $"模型：{node.ModelPath}\n训练目录：{node.RunDirectory}";
        TrainingStatusText.Text = $"已加载模型：{node.Name}（只读）";
        DrawLossCurve();
        SetModelEditorState(false);
        SetTrainingState(false);
    }

    private void SetModelEditorState(bool editable)
    {
        EpochsText.IsReadOnly = !editable;
        ImageSizeText.IsReadOnly = !editable;
        BatchSizeCombo.IsEnabled = editable;
        DeviceCombo.IsEnabled = editable;
        ModelCombo.IsEnabled = editable;
        ExportTensorRtCheckBox.IsEnabled = editable;
    }

    private static string? ReadYamlValue(string path, string key)
    {
        if (!File.Exists(path)) return null;
        var prefix = key + ":";
        var line = File.ReadLines(path).FirstOrDefault(value => value.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
    }

    private static int? ReadYamlInt(string path, string key) =>
        int.TryParse(ReadYamlValue(path, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static List<LossPoint> ReadLossPoints(string path)
    {
        var points = new List<LossPoint>();
        if (!File.Exists(path)) return points;
        try
        {
            var rows = File.ReadAllLines(path);
            if (rows.Length < 2) return points;
            var headers = rows[0].Split(',').Select(x => x.Trim()).ToArray();
            var epochIndex = Array.FindIndex(headers, x => x.Equals("epoch", StringComparison.OrdinalIgnoreCase));
            var lossIndexes = headers.Select((value, index) => (value, index))
                .Where(item => item.value.Contains("loss", StringComparison.OrdinalIgnoreCase)
                               && item.value.StartsWith("train", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index).ToArray();
            if (epochIndex < 0 || lossIndexes.Length == 0) return points;
            foreach (var row in rows.Skip(1))
            {
                var values = row.Split(',');
                if (values.Length <= epochIndex || !int.TryParse(values[epochIndex].Trim(), out var epoch)) continue;
                var losses = lossIndexes
                    .Where(index => index < values.Length)
                    .Select(index => double.TryParse(values[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var loss) ? loss : double.NaN)
                    .Where(double.IsFinite)
                    .ToArray();
                if (losses.Length > 0) points.Add(new LossPoint { Epoch = epoch, Loss = losses.Sum() });
            }

            // 部分训练器从第 0 轮开始记录，统一转换为用户看到的第 1 轮开始。
            if (points.Count > 0 && points.Min(point => point.Epoch) == 0)
            {
                foreach (var point in points) point.Epoch++;
            }
        }
        catch { }
        return points;
    }

    private static void NormalizeLossPoints(TrainingModelNode node)
    {
        if (node.Epochs <= 0 || node.LossPoints.Count == 0) return;
        var maxEpoch = node.LossPoints.Max(point => point.Epoch);
        if (maxEpoch == node.Epochs + 1)
        {
            foreach (var point in node.LossPoints) point.Epoch--;
        }

        node.LossPoints = node.LossPoints
            .Where(point => point.Epoch > 0 && point.Epoch <= node.Epochs)
            .ToList();
    }

    private static void SaveModelNode(TrainingModelNode node)
    {
        if (string.IsNullOrWhiteSpace(node.RunDirectory)) return;
        Directory.CreateDirectory(node.RunDirectory);
        var metadataPath = IoPath.Combine(node.RunDirectory, "model-node.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = RefreshBatchOptionsAsync();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = RefreshBatchOptionsAsync();

    private void ImageSizeText_LostFocus(object sender, RoutedEventArgs e) => _ = RefreshBatchOptionsAsync();

    private async Task RefreshBatchOptionsAsync()
    {
        if (BatchSizeCombo is null) return;
        var requestId = Interlocked.Increment(ref _batchOptionsRequestId);
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
        if (requestId != Volatile.Read(ref _batchOptionsRequestId)) return;
        ExportTensorRtCheckBox.Visibility = gpu.Available ? Visibility.Visible : Visibility.Collapsed;
        if (!gpu.Available) ExportTensorRtCheckBox.IsChecked = false;
        var maxBatch = EstimateMaxBatch(gpu, device, imageSize);
        var options = new[] { 1, 2, 4, 8, 16, 32, 64 }.Where(batch => batch <= maxBatch).ToList();
        if (options.Count == 0) options.Add(1);
        SetBatchSizeOptions(options, options.Contains(previous) ? previous : options.Contains(2) ? 2 : options[0]);
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

    private void SetBatchSizeOptions(IReadOnlyList<int> options, int selected)
    {
        BatchSizeCombo.ItemsSource = options.ToList();
        BatchSizeCombo.SelectedItem = options.Contains(selected) ? selected : options.FirstOrDefault();
        if (BatchSizeCombo.SelectedItem is null && BatchSizeCombo.Items.Count > 0)
            BatchSizeCombo.SelectedIndex = 0;
    }

    private int EstimateMaxBatch(Yolo11GpuMemory gpu, string device, int imageSize)
    {
        if (device == "cpu" || !gpu.Available) return 8;
        var freeGiB = gpu.FreeBytes / 1024d / 1024d / 1024d;
        var imageFactor = Math.Pow(imageSize / 640d, 2);
        var modelPath = (ModelCombo.SelectedItem as ModelOption)?.ModelPath ?? "yolo11n.pt";
        var modelFactor = modelPath.Contains("11x", StringComparison.OrdinalIgnoreCase) ? 5.0
            : modelPath.Contains("11l", StringComparison.OrdinalIgnoreCase) ? 3.5
            : modelPath.Contains("11m", StringComparison.OrdinalIgnoreCase) ? 2.5
            : modelPath.Contains("s", StringComparison.OrdinalIgnoreCase) ? 1.6
            : 1.0;
        var estimatedPerBatchGiB = 0.8 * imageFactor * modelFactor;
        var rawMaximum = (int)Math.Floor(freeGiB * 0.65 / estimatedPerBatchGiB);
        var maximum = 1;
        while (maximum * 2 <= rawMaximum && maximum < 64) maximum *= 2;
        return maximum;
    }

    private async void StartTraining_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) return;
        if (!_isNewModel)
        {
            ThemedMessageBox.Show("已完成训练的模型节点为只读状态，请先点击“新建模型”再开始训练。", "训练", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_dataset.TaskType == "semantic_segmentation")
        {
            await StartAtu5TrainingAsync();
            return;
        }
        if (_dataset.TaskType == "semantic_segmentation" && false)
        {
            ThemedMessageBox.Show("YOLO11 不支持原生语义分割训练。请改用目标检测/实例分割数据集，或后续接入 YOLO26-sem。", "无法开始训练", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ModelCombo.SelectedItem is not ModelOption model)
        {
            ThemedMessageBox.Show("请先选择预训练模型或数据集目录中的已训练模型。", "训练", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryParsePositive(EpochsText.Text, "学习次数", out var epochs) ||
            !TryParsePositive(ImageSizeText.Text, "图像尺寸", out var imageSize)) return;
        if (BatchSizeCombo.SelectedItem is not int batchSize)
        {
            ThemedMessageBox.Show("请选择批大小。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            PauseButton.IsEnabled = true;
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
                model.IsExisting,
                ExportTensorRtCheckBox.IsChecked == true);
            var progress = new Progress<Yolo11TrainingProgress>(HandleProgress);
            var result = await AppServices.Instance.Yolo11Training.RunAsync(request, progress, _trainingCancellation.Token);
            OutputPathText.Text = $"训练完成：{result.ModelPath}";
            TrainingStatusText.Text = "训练完成";
            AddLog("训练完成，模型和曲线已保存到数据集目录。");
            if (!string.IsNullOrWhiteSpace(result.EnginePath))
            {
                AddLog($"TensorRT Engine 已导出：{result.EnginePath}");
                OutputPathText.Text += $"\nEngine：{result.EnginePath}";
            }
            SaveModelNode(new TrainingModelNode
            {
                Name = runName,
                DatasetRoot = _dataset.RootDirectory,
                TaskType = _dataset.TaskType,
                BaseModel = model.ModelPath,
                ModelPath = result.ModelPath,
                RunDirectory = result.RunDirectory,
                EnginePath = result.EnginePath,
                CompletedAt = DateTime.Now,
                Epochs = epochs,
                BatchSize = batchSize,
                ImageSize = imageSize,
                Device = device,
                LossPoints = _lossPoints.Select(point => new LossPoint { Epoch = point.Epoch, Loss = point.Loss }).ToList(),
                Logs = _trainingLogs.ToList(),
            });
            RefreshModels();
            RefreshModelNodes(result.ModelPath);
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
            ThemedMessageBox.Show(ex.Message, "YOLO11 训练失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _trainingCancellation?.Dispose();
            _trainingCancellation = null;
            SetTrainingState(false);
        }
    }

    private async Task StartAtu5TrainingAsync()
    {
        if (_dataset is null || ModelCombo.SelectedItem is not ModelOption model) return;
        var initialWeight = "models/atu5.pt";
        if (!AppServices.Instance.Atu5Training.IsModelAvailable(initialWeight))
        {
            ThemedMessageBox.Show($"ATU5 初始权重不存在：{initialWeight}", "ATU5 训练", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParsePositive(EpochsText.Text, "训练轮数", out var epochs) ||
            !TryParsePositive(ImageSizeText.Text, "图像尺寸", out var imageSize)) return;
        if (BatchSizeCombo.SelectedItem is not int batchSize)
        {
            ThemedMessageBox.Show("请选择批大小。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var trainingDataDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "training-data", "atu5-" + stamp);
        var outputDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
        try
        {
            SetTrainingState(true);
            ResetChart();
            TrainingStatusText.Text = "正在整理语义分割标注…";
            AddLog($"任务：语义分割（ATU5 / FPN）；模型：{model.DisplayName}");
            var manifestPath = await Task.Run(() => AppServices.Instance.Datasets.ExportSemantic(_dataset, trainingDataDirectory));
            AddLog($"语义分割训练清单已生成：{manifestPath}");
            _trainingCancellation = new CancellationTokenSource();
            PauseButton.IsEnabled = true;
            var request = new Atu5TrainingRequest(manifestPath, initialWeight, epochs, batchSize, imageSize,
                device, outputDirectory, $"{SanitizeFileName(_dataset.Name)}-semantic_segmentation-{stamp}",
                ExportTensorRtCheckBox.IsChecked == true);
            var progress = new Progress<Yolo11TrainingProgress>(HandleProgress);
            var result = await AppServices.Instance.Atu5Training.RunAsync(request, progress, _trainingCancellation.Token);
            OutputPathText.Text = $"ATU5 训练完成：{result.ModelPath}";
            TrainingStatusText.Text = "ATU5 训练完成";
            AddLog("ATU5 语义分割模型和训练曲线已保存。");
            if (!string.IsNullOrWhiteSpace(result.EnginePath))
            {
                AddLog($"TensorRT Engine 已导出：{result.EnginePath}");
                OutputPathText.Text += $"\nEngine：{result.EnginePath}";
            }
            SaveModelNode(new TrainingModelNode
            {
                Name = IoPath.GetFileName(result.RunDirectory),
                DatasetRoot = _dataset.RootDirectory,
                TaskType = _dataset.TaskType,
                BaseModel = model.ModelPath,
                ModelPath = result.ModelPath,
                RunDirectory = result.RunDirectory,
                EnginePath = result.EnginePath,
                CompletedAt = DateTime.Now,
                Epochs = epochs,
                BatchSize = batchSize,
                ImageSize = imageSize,
                Device = device,
                LossPoints = _lossPoints.Select(point => new LossPoint { Epoch = point.Epoch, Loss = point.Loss }).ToList(),
                Logs = _trainingLogs.ToList(),
            });
            RefreshModels();
            RefreshModelNodes(result.ModelPath);
        }
        catch (OperationCanceledException)
        {
            TrainingStatusText.Text = "训练已停止";
            AddLog("用户停止了 ATU5 训练。");
        }
        catch (Exception ex)
        {
            TrainingStatusText.Text = "训练失败";
            AddLog($"ATU5 训练失败：{ex.Message}");
            ThemedMessageBox.Show(ex.Message, "ATU5 训练失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        TrainingStatusText.Text = "正在终止训练…";
        if (AppServices.Instance.Yolo11Training.IsRunning) await AppServices.Instance.Yolo11Training.CancelAsync();
        if (AppServices.Instance.Atu5Training.IsRunning) await AppServices.Instance.Atu5Training.CancelAsync();
        _trainingCancellation?.Cancel();
    }

    private async void PauseTraining_Click(object sender, RoutedEventArgs e)
    {
        var semantic = _dataset?.TaskType == "semantic_segmentation";
        var isPaused = semantic
            ? AppServices.Instance.Atu5Training.IsPaused
            : AppServices.Instance.Yolo11Training.IsPaused;

        if (!_trainingPaused && !isPaused)
        {
            var paused = semantic
                ? await AppServices.Instance.Atu5Training.PauseAsync()
                : await AppServices.Instance.Yolo11Training.PauseAsync();
            if (!paused)
            {
                TrainingStatusText.Text = "训练进程尚未启动，暂时无法暂停。";
                return;
            }

            _trainingPaused = true;
            PauseButton.Content = "继续训练";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            TrainingStatusText.Text = "训练已暂停。点击“继续训练”恢复。";
            AddLog("训练已暂停。");
            return;
        }

        var resumed = semantic
            ? await AppServices.Instance.Atu5Training.ResumeAsync()
            : await AppServices.Instance.Yolo11Training.ResumeAsync();
        if (!resumed)
        {
            TrainingStatusText.Text = "训练进程无法恢复，可能已经终止。";
            return;
        }

        _trainingPaused = false;
        PauseButton.Content = "暂停训练";
        StopButton.IsEnabled = true;
        TrainingStatusText.Text = "训练已继续。";
        AddLog("训练已继续。");
    }

    private void OpenLocalDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || string.IsNullOrWhiteSpace(_dataset.RootDirectory))
        {
            ThemedMessageBox.Show("请先选择一个数据集。", "打开本地目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(_dataset.RootDirectory))
        {
            ThemedMessageBox.Show($"数据集目录不存在：{_dataset.RootDirectory}", "打开本地目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var modelDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
        var targetDirectory = Directory.Exists(modelDirectory) ? modelDirectory : _dataset.RootDirectory;
        Process.Start(new ProcessStartInfo
        {
            FileName = targetDirectory,
            UseShellExecute = true,
        });
        if (targetDirectory != modelDirectory)
            TrainingStatusText.Text = "模型目录尚未生成，已打开数据集本地目录。";
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
        if (!running)
        {
            _trainingPaused = false;
            PauseButton.Content = "暂停训练";
        }
        StartButton.IsEnabled = !running && _isNewModel && _dataset?.TaskType is ("detection" or "instance_segmentation" or "semantic_segmentation");
        StopButton.IsEnabled = running;
        PauseButton.IsEnabled = false;
        DatasetCombo.IsEnabled = !running;
        NewModelButton.IsEnabled = !running;
        ModelNodeList.IsEnabled = !running;
        ModelCombo.IsEnabled = !running;
        BatchSizeCombo.IsEnabled = !running;
        ImageSizeText.IsEnabled = !running;
        DeviceCombo.IsEnabled = !running;
        SetModelEditorState(!running && _isNewModel);
    }

    private void ResetChart()
    {
        _lossPoints.Clear();
        _trainingLogs.Clear();
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
        const double bottom = 42;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var axisBrush = ThemeBrush("BorderBrush", Colors.Gray);
        LossCanvas.Children.Add(new Line { X1 = left, Y1 = top, X2 = left, Y2 = height - bottom, Stroke = axisBrush });
        LossCanvas.Children.Add(new Line { X1 = left, Y1 = height - bottom, X2 = width - right, Y2 = height - bottom, Stroke = axisBrush });

        var maxEpoch = Math.Max(1, _lossPoints.Count == 0 ? 1 : _lossPoints.Max(point => point.Epoch));
        const int epochTickCount = 4;
        var tickBrush = ThemeBrush("MutedTextBrush", Colors.Gray);
        for (var index = 0; index <= epochTickCount; index++)
        {
            var ratio = index / (double)epochTickCount;
            var x = left + ratio * plotWidth;
            var epoch = index == epochTickCount
                ? maxEpoch
                : Math.Max(1, (int)Math.Round(ratio * maxEpoch));
            var label = new TextBlock
            {
                Text = epoch.ToString(CultureInfo.InvariantCulture),
                Foreground = tickBrush,
                FontSize = 10,
                Width = 44,
                TextAlignment = TextAlignment.Center,
            };
            LossCanvas.Children.Add(label);
            Canvas.SetLeft(label, Math.Clamp(x - label.Width / 2, 0, Math.Max(0, width - label.Width)));
            Canvas.SetTop(label, height - bottom + 5);
        }

        var axisTitle = new TextBlock
        {
            Text = "Epoch",
            Foreground = tickBrush,
            FontSize = 10,
            Width = 70,
            TextAlignment = TextAlignment.Center,
        };
        LossCanvas.Children.Add(axisTitle);
        Canvas.SetLeft(axisTitle, Math.Clamp((left + width - right - axisTitle.Width) / 2, 0, Math.Max(0, width - axisTitle.Width)));
        Canvas.SetTop(axisTitle, height - 18);

        if (_lossPoints.Count == 0) return;

        // 根据当前数据动态收窄纵轴范围，避免 Loss 都挤在图表顶部。
        var losses = _lossPoints
            .Select(point => point.Loss)
            .Where(double.IsFinite)
            .ToArray();
        if (losses.Length == 0) return;
        var dataMin = losses.Min();
        var dataMax = losses.Max();
        var dataRange = dataMax - dataMin;
        var padding = dataRange > 0.000001
            ? dataRange * 0.15
            : Math.Max(Math.Abs(dataMax) * 0.1, 0.001);
        var minLoss = Math.Max(0, dataMin - padding);
        var maxLoss = Math.Max(minLoss + 0.001, dataMax + padding);
        var axisRange = maxLoss - minLoss;
        var decimalPlaces = axisRange >= 1 ? 2 : axisRange >= 0.1 ? 3 : axisRange >= 0.01 ? 4 : axisRange >= 0.001 ? 5 : 6;
        var axisFormat = "0." + new string('#', decimalPlaces);
        var gridBrush = ThemeBrush("GridLineBrush", Colors.LightGray);
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
                Text = value.ToString(axisFormat),
                Foreground = ThemeBrush("MutedTextBrush", Colors.Gray),
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
            if (!double.IsFinite(point.Loss)) continue;
            var y = top + (maxLoss - point.Loss) * plotHeight / axisRange;
            polyline.Points.Add(new Point(x, y));
        }
        LossCanvas.Children.Add(polyline);
    }

    private void AddLog(string message)
    {
        var log = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _trainingLogs.Add(log);
        TrainingLogList.Items.Add(log);
        if (TrainingLogList.Items.Count > 300) TrainingLogList.Items.RemoveAt(0);
        if (_trainingLogs.Count > 300) _trainingLogs.RemoveAt(0);
        TrainingLogList.ScrollIntoView(TrainingLogList.Items[^1]);
    }

    private static Brush ThemeBrush(string key, Color fallback)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(fallback);
    }

    private static bool TryParsePositive(string text, string name, out int value)
    {
        if (int.TryParse(text, out value) && value > 0) return true;
        ThemedMessageBox.Show($"{name}必须是大于 0 的整数。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = new string(IoPath.GetInvalidFileNameChars());
        return string.Join("_", value.Split(invalid.ToCharArray(), StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class TrainingModelNode
    {
        public string Name { get; set; } = "未命名模型";
        public string DatasetRoot { get; set; } = "";
        public string TaskType { get; set; } = "";
        public string BaseModel { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string RunDirectory { get; set; } = "";
        public string? EnginePath { get; set; }
        public DateTime CompletedAt { get; set; }
        public int Epochs { get; set; }
        public int BatchSize { get; set; }
        public int ImageSize { get; set; }
        public string Device { get; set; } = "";
        public List<LossPoint> LossPoints { get; set; } = [];
        public List<string> Logs { get; set; } = [];
        public string DisplayName => $"{Name}\n已完成 · {CompletedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private sealed class LossPoint
    {
        public int Epoch { get; set; }
        public double Loss { get; set; }
    }

    private sealed record ModelOption(string DisplayName, string ModelPath, bool IsExisting)
    {
        public override string ToString() => DisplayName;
    }
}
