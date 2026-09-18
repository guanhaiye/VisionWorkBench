using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private DateTime _lastNewModelClickUtc;
    private readonly DispatcherTimer _gpuMemoryTimer;
    private int _gpuMemoryQueryInFlight;
    private double _lossChartTop;
    private double _lossChartHeight;
    private double _lossChartMin;
    private double _lossChartMax;
    private string _lossChartFormat = "0.###";
    private bool _lossChartHasData;
    private Line? _lossHoverLine;
    private TextBlock? _lossHoverLabel;

    public TrainingPage(string? taskTypeFilter = null)
    {
        _taskTypeFilter = taskTypeFilter;
        InitializeComponent();
        _gpuMemoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gpuMemoryTimer.Tick += async (_, _) => await RefreshGpuMemoryTextAsync();
        SetBatchSizeOptions([1, 2, 4, 8], 2);
        Loaded += async (_, _) =>
        {
            _gpuMemoryTimer.Start();
            RefreshDatasets();
            await RefreshBatchOptionsAsync();
            await RefreshGpuMemoryTextAsync();
        };
        Unloaded += (_, _) => _gpuMemoryTimer.Stop();
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
        if (datasets.Count == 0)
        {
            SetTaskTypeUi(null);
            return;
        }

        var preferredId = AppServices.Instance.Settings.LastTrainingDatasetId;
        var preferredIndex = datasets.FindIndex(dataset =>
            string.Equals(dataset.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        DatasetCombo.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
    }

    private void DatasetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _dataset = DatasetCombo.SelectedItem as DatasetDefinition;
        if (_dataset is not null &&
            !string.Equals(AppServices.Instance.Settings.LastTrainingDatasetId, _dataset.Id, StringComparison.OrdinalIgnoreCase))
        {
            AppServices.Instance.Settings.LastTrainingDatasetId = _dataset.Id;
            AppServices.Instance.SaveUserSettings();
        }
        SetTaskTypeUi(_dataset);
        RefreshDatasetSplitSummary();
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
            "pose" => "关键点检测（YOLO11 pose）",
            "semantic_segmentation" => "语义分割（YOLO11 不支持原生训练）",
            _ => "未选择数据集",
        };
        TaskTypeText.Text = $"任务：{taskName}";
        TaskTypeText.Foreground = Brushes.DarkGreen;
        if (taskType == "semantic_segmentation") TaskTypeText.Text = "任务：语义分割（ATU5 / FPN）";
        StartButton.IsEnabled = CanStartTraining();
        StartButton.ToolTip = _selectedModelNode is null
            ? "请先选择或新建模型节点。"
            : null;
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
        else if (taskType == "pose")
        {
            options.AddRange([
                OfficialModel("YOLO11n-pose", "models/yolo11n-pose.pt"),
                OfficialModel("YOLO11s-pose", "models/yolo11s-pose.pt"),
                OfficialModel("YOLO11m-pose", "models/yolo11m-pose.pt"),
                OfficialModel("YOLO11l-pose", "models/yolo11l-pose.pt"),
                OfficialModel("YOLO11x-pose", "models/yolo11x-pose.pt"),
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

    private void RefreshDatasetSplitSummary()
    {
        if (DatasetSplitText is null) return;
        if (_dataset is null)
        {
            DatasetSplitText.Text = "训练集：-，验证集：-";
            return;
        }

        try
        {
            var images = AppServices.Instance.Datasets.ListImages(_dataset);
            var train = images.Count(image => image.Split == "train");
            var validation = images.Count(image => image.Split == "val");
            var unassigned = images.Count - train - validation;
            DatasetSplitText.Text = unassigned > 0
                ? $"训练集：{train} 张，验证集：{validation} 张，未划分：{unassigned} 张"
                : $"训练集：{train} 张，验证集：{validation} 张";
        }
        catch
        {
            DatasetSplitText.Text = "训练集：读取失败，验证集：读取失败";
        }
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
        else if (string.IsNullOrWhiteSpace(selected.ModelPath)) ActivateNewModel(selected);
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
                if (node is not null && !string.IsNullOrWhiteSpace(node.Name)
                    && string.Equals(node.TaskType, _dataset?.TaskType, StringComparison.OrdinalIgnoreCase))
                {
                    node.RunDirectory = runDirectory;
                    // 训练进程可能已经写出权重，但桌面端在写回节点元数据前被关闭。
                    // 发现同目录已有权重时，将草稿恢复为已完成节点，避免重启后重复显示。
                    if (string.IsNullOrWhiteSpace(node.ModelPath)
                        && FindTrainingModelFile(runDirectory) is { } recoveredModelPath)
                    {
                        node.ModelPath = recoveredModelPath;
                        node.CompletedAt = Directory.GetLastWriteTime(runDirectory);
                        if (node.LossPoints.Count == 0)
                            node.LossPoints = ReadLossPoints(IoPath.Combine(runDirectory, "results.csv"));
                        try { SaveModelNode(node); } catch { }
                    }
                    NormalizeLossPoints(node);
                    return node;
                }
            }

            var modelPath = FindTrainingModelFile(runDirectory);
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

    private static string? FindTrainingModelFile(string runDirectory) =>
        Directory.EnumerateFiles(runDirectory, "best.pt", SearchOption.AllDirectories).FirstOrDefault()
        ?? Directory.EnumerateFiles(runDirectory, "last.pt", SearchOption.AllDirectories).FirstOrDefault();

    private string GetTrainingRunName(string fallback)
    {
        if (!_isNewModel || _selectedModelNode is null || string.IsNullOrWhiteSpace(_selectedModelNode.RunDirectory)
            || _dataset is null) return fallback;

        try
        {
            var modelRoot = IoPath.GetFullPath(IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models"));
            var draftDirectory = IoPath.GetFullPath(_selectedModelNode.RunDirectory);
            var modelRootPrefix = modelRoot.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar)
                                  + IoPath.DirectorySeparatorChar;
            if (draftDirectory.StartsWith(modelRootPrefix, StringComparison.OrdinalIgnoreCase))
                return IoPath.GetFileName(draftDirectory);
        }
        catch
        {
            // 目录异常时使用默认训练名称。
        }

        return fallback;
    }

    private string GetCurrentNodeName(string fallback) =>
        _isNewModel && _selectedModelNode is { Name.Length: > 0 } node ? node.Name : fallback;

    private void NewModel_Click(object sender, RoutedEventArgs e)
    {
        // 防止鼠标连点或重复路由事件在短时间内创建多个草稿节点。
        var now = DateTime.UtcNow;
        if ((now - _lastNewModelClickUtc).TotalMilliseconds < 2000) return;
        _lastNewModelClickUtc = now;
        e.Handled = true;
        BeginNewModel(addToList: true);
    }

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

    private void RenameModelNodeMenu_Click(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedModelNode();
        if (node is null) return;

        var dialog = new ModelRenameDialog(node.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || string.Equals(dialog.ModelName, node.Name, StringComparison.Ordinal)) return;

        node.Name = dialog.ModelName;
        if (!string.IsNullOrWhiteSpace(node.RunDirectory))
        {
            try
            {
                SaveModelNode(node);
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"保存模型名称失败：{ex.Message}", "重命名模型节点", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        ModelNodeList.Items.Refresh();
        ModelNodeList.SelectedItem = node;
        TrainingStatusText.Text = $"模型节点已重命名为：{node.Name}";
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

    private void ClearModelNodesMenu_Click(object sender, RoutedEventArgs e)
    {
        var nodes = _modelNodes.ToList();
        if (nodes.Count == 0)
        {
            TrainingStatusText.Text = "当前没有可清空的模型节点。";
            return;
        }

        var savedCount = nodes.Count(node => !string.IsNullOrWhiteSpace(node.RunDirectory));
        var answer = ThemedMessageBox.Show(
            $"确定清空当前数据集的全部模型节点吗？\n\n将移除 {nodes.Count} 个节点，其中 {savedCount} 个训练目录会移入回收站。",
            "清空模型节点",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            dangerConfirmation: true);
        if (answer != MessageBoxResult.Yes) return;

        var modelRoot = _dataset is null
            ? ""
            : IoPath.GetFullPath(IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models"));
        var modelRootPrefix = modelRoot.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar)
                              + IoPath.DirectorySeparatorChar;
        try
        {
            foreach (var node in nodes)
            {
                if (string.IsNullOrWhiteSpace(node.RunDirectory) || string.IsNullOrWhiteSpace(modelRoot)) continue;
                var runDirectory = IoPath.GetFullPath(node.RunDirectory);
                if (!runDirectory.StartsWith(modelRootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(runDirectory))
                {
                    VbFileSystem.DeleteDirectory(runDirectory,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }

            RefreshModelNodes();
            TrainingStatusText.Text = "模型节点已清空，训练目录已移入回收站。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"清空模型节点失败：{ex.Message}", "清空模型节点", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (_updatingModelNodes) return;
        if (ModelNodeList.SelectedItem is not TrainingModelNode node)
        {
            _selectedModelNode = null;
            _isNewModel = false;
            SetTrainingState(false);
            return;
        }

        CaptureCurrentDraftState();
        if (string.IsNullOrWhiteSpace(node.ModelPath))
            ActivateNewModel(node);
        else
            LoadModelNodeIntoEditor(node);
    }

    private void BeginNewModel(bool addToList = false)
    {
        CaptureCurrentDraftState();
        TrainingModelNode? draft = null;
        if (addToList)
        {
            // 新建节点先作为当前列表项显示，训练成功后会由真实的模型节点替换它。
            var draftName = $"{SanitizeFileName(_dataset?.Name ?? "模型")}-新建-{DateTime.Now:yyyyMMdd-HHmmssfff}";
            draft = new TrainingModelNode
            {
                Name = draftName,
                DatasetRoot = _dataset?.RootDirectory ?? "",
                TaskType = _dataset?.TaskType ?? "",
                BaseModel = (ModelCombo.SelectedItem as ModelOption)?.ModelPath ?? "",
                Epochs = 100,
                BatchSize = 2,
                ImageSize = 0,
                Device = "auto",
                ExportTensorRt = true,
            };
            if (_dataset is not null)
            {
                var modelDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
                var draftDirectory = IoPath.Combine(modelDirectory, draftName);
                var suffix = 2;
                while (Directory.Exists(draftDirectory))
                    draftDirectory = IoPath.Combine(modelDirectory, $"{draftName}-{suffix++}");
                draft.RunDirectory = draftDirectory;
                SaveModelNode(draft);
            }
            // List 不会向 WPF 通知集合变化；替换列表实例后重新绑定，确保新节点立即显示。
            _modelNodes = _modelNodes.ToList();
            _modelNodes.Insert(0, draft);
        }

        ActivateNewModel(draft);
    }

    private void ActivateNewModel(TrainingModelNode? draft)
    {
        _selectedModelNode = draft;
        _isNewModel = true;
        _updatingModelNodes = true;
        ModelNodeList.ItemsSource = _modelNodes;
        ModelNodeList.SelectedItem = draft;
        _updatingModelNodes = false;
        var epochs = draft?.Epochs > 0 ? draft.Epochs : 100;
        var imageSize = draft?.ImageSize ?? 0;
        var batchSize = draft?.BatchSize > 0 ? draft.BatchSize : 2;
        EpochsText.Text = epochs.ToString();
        SetImageSizeOption(imageSize);
        SetBatchSizeOptions([1, 2, 4, 8], batchSize);
        DeviceCombo.SelectedItem = DeviceCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, draft?.Device ?? "auto", StringComparison.OrdinalIgnoreCase))
            ?? DeviceCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        ExportTensorRtCheckBox.IsChecked = draft?.ExportTensorRt ?? true;
        if (!string.IsNullOrWhiteSpace(draft?.BaseModel))
        {
            ModelCombo.SelectedItem = ModelCombo.Items.OfType<ModelOption>()
                .FirstOrDefault(option => string.Equals(option.ModelPath, draft.BaseModel, StringComparison.OrdinalIgnoreCase));
        }
        else if (ModelCombo.Items.Count > 0)
        {
            ModelCombo.SelectedIndex = 0;
        }
        _lossPoints.Clear();
        _lossPoints.AddRange(draft?.LossPoints.Select(point => (point.Epoch, point.Loss)) ?? []);
        TrainingLogList.Items.Clear();
        _trainingLogs.Clear();
        if (draft is not null)
        {
            _trainingLogs.AddRange(draft.Logs);
            foreach (var log in draft.Logs) TrainingLogList.Items.Add(log);
        }
        CurrentLossText.Text = _lossPoints.Count > 0
            ? $"已加载 {_lossPoints.Count} 个训练点"
            : "暂无训练数据";
        DrawLossCurve();
        OutputPathText.Text = "";
        TrainingStatusText.Text = "已新建模型，可设置训练超参数。";
        SetModelEditorState(true);
        SetTrainingState(false);
    }

    private void CaptureCurrentDraftState()
    {
        if (!_isNewModel || _selectedModelNode is null) return;

        var node = _selectedModelNode;
        node.BaseModel = (ModelCombo.SelectedItem as ModelOption)?.ModelPath ?? node.BaseModel;
        node.Epochs = int.TryParse(EpochsText.Text, out var epochs) && epochs > 0 ? epochs : node.Epochs;
        node.BatchSize = BatchSizeCombo.SelectedItem is int batchSize ? batchSize : node.BatchSize;
        node.ImageSize = GetSelectedImageSize();
        node.Device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? node.Device;
        node.ExportTensorRt = ExportTensorRtCheckBox.IsChecked == true;
        node.LossPoints = _lossPoints
            .Select(point => new LossPoint { Epoch = point.Epoch, Loss = point.Loss })
            .ToList();
        node.Logs = _trainingLogs.ToList();
        try
        {
            SaveModelNode(node);
        }
        catch
        {
            // 草稿仍保留在内存中；磁盘写入失败不应阻断节点切换。
        }
    }

    private void LoadModelNodeIntoEditor(TrainingModelNode node)
    {
        _selectedModelNode = node;
        _isNewModel = false;
        EpochsText.Text = node.Epochs > 0 ? node.Epochs.ToString() : "—";
        SetImageSizeOption(node.ImageSize);
        if (node.BatchSize > 0) SetBatchSizeOptions([node.BatchSize], node.BatchSize);
        if (!string.IsNullOrWhiteSpace(node.Device))
        {
            DeviceCombo.SelectedItem = DeviceCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, node.Device, StringComparison.OrdinalIgnoreCase));
        }
        ExportTensorRtCheckBox.IsChecked = node.ExportTensorRt;
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
        ImageSizeCombo.IsEnabled = editable;
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

    private void RemoveCurrentDraftStorage(string? replacementRunDirectory = null)
    {
        var draftDirectory = _selectedModelNode?.RunDirectory;
        if (_selectedModelNode is null || !string.IsNullOrWhiteSpace(_selectedModelNode.ModelPath)
            || string.IsNullOrWhiteSpace(draftDirectory) || _dataset is null) return;

        if (!string.IsNullOrWhiteSpace(replacementRunDirectory)
            && string.Equals(IoPath.GetFullPath(draftDirectory), IoPath.GetFullPath(replacementRunDirectory),
                StringComparison.OrdinalIgnoreCase))
            return;

        var modelRoot = IoPath.GetFullPath(IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models"));
        var draftPath = IoPath.GetFullPath(draftDirectory);
        var modelRootPrefix = modelRoot.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar)
                              + IoPath.DirectorySeparatorChar;
        if (!draftPath.StartsWith(modelRootPrefix, StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(draftPath)) Directory.Delete(draftPath, recursive: true);
        _selectedModelNode.RunDirectory = "";
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

    private void ImageSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = RefreshBatchOptionsAsync();

    private async Task RefreshBatchOptionsAsync()
    {
        if (BatchSizeCombo is null) return;
        var requestId = Interlocked.Increment(ref _batchOptionsRequestId);
        var previous = BatchSizeCombo.SelectedItem is int value ? value : 2;
        var imageSize = ResolveTrainingImageSize(GetSelectedImageSize());
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

    private async Task RefreshGpuMemoryTextAsync()
    {
        if (GpuMemoryText is null || Interlocked.Exchange(ref _gpuMemoryQueryInFlight, 1) != 0) return;
        try
        {
            var gpu = await AppServices.Instance.Yolo11Training.QueryGpuMemoryAsync();
            if (!gpu.Available || gpu.TotalBytes <= 0)
            {
                GpuMemoryText.Text = "GPU 显存：未检测到 CUDA GPU";
                return;
            }

            var usedBytes = Math.Max(0, gpu.TotalBytes - gpu.FreeBytes);
            var usedGiB = usedBytes / 1024d / 1024d / 1024d;
            var totalGiB = gpu.TotalBytes / 1024d / 1024d / 1024d;
            var percentage = usedBytes * 100d / gpu.TotalBytes;
            GpuMemoryText.Text = $"GPU 显存：已用 {usedGiB:0.00} / {totalGiB:0.00} GB（{percentage:0.0}%）";
        }
        catch
        {
            GpuMemoryText.Text = "GPU 显存：读取失败";
        }
        finally
        {
            Interlocked.Exchange(ref _gpuMemoryQueryInFlight, 0);
        }
    }

    private void SetBatchSizeOptions(IReadOnlyList<int> options, int selected)
    {
        BatchSizeCombo.ItemsSource = options.ToList();
        BatchSizeCombo.SelectedItem = options.Contains(selected) ? selected : options.FirstOrDefault();
        if (BatchSizeCombo.SelectedItem is null && BatchSizeCombo.Items.Count > 0)
            BatchSizeCombo.SelectedIndex = 0;
    }

    private int GetSelectedImageSize()
    {
        if (ImageSizeCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) &&
            size is 512 or 1024)
        {
            return size;
        }
        return 0;
    }

    private void SetImageSizeOption(int imageSize)
    {
        var tag = imageSize is 512 or 1024 ? imageSize.ToString(CultureInfo.InvariantCulture) : "original";
        ImageSizeCombo.SelectedItem = ImageSizeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (ImageSizeCombo.SelectedItem is null && ImageSizeCombo.Items.Count > 0)
            ImageSizeCombo.SelectedIndex = 0;
    }

    private bool TryGetTrainingImageSize(out int selectedImageSize, out int effectiveImageSize)
    {
        selectedImageSize = GetSelectedImageSize();
        effectiveImageSize = ResolveTrainingImageSize(selectedImageSize);
        if (effectiveImageSize > 0) return true;

        ThemedMessageBox.Show("无法确定训练输入图像尺寸，请先选择有效的图像尺寸。", "训练参数",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private int ResolveTrainingImageSize(int selectedImageSize)
    {
        if (selectedImageSize > 0) return selectedImageSize;
        if (_dataset is not null)
        {
            var source = AppServices.Instance.Datasets.ListImages(_dataset)
                .FirstOrDefault(image => image.Split is "train" or "val" && File.Exists(image.FullPath));
            if (source is not null)
            {
                try
                {
                    using var stream = File.OpenRead(source.FullPath);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var side = Math.Max(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
                    if (side > 0)
                        return Math.Max(32, (int)Math.Ceiling(side / 32d) * 32);
                }
                catch
                {
                    // The worker will report an actionable image-read error if the source is invalid.
                }
            }
        }
        return 640;
    }

    private static string FormatImageSize(int imageSize) => imageSize switch
    {
        512 => "512",
        1024 => "1024",
        _ => "原图大小",
    };

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

    private async Task StartTrainingAsync()
    {
        if (_dataset is null || _selectedModelNode is null) return;
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
            !TryGetTrainingImageSize(out var selectedImageSize, out var imageSize)) return;
        if (BatchSizeCombo.SelectedItem is not int batchSize)
        {
            ThemedMessageBox.Show("请选择批大小。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var trainingDataDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "training-data", stamp);
        var outputDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
        var runName = GetTrainingRunName($"{SanitizeFileName(_dataset.Name)}-{_dataset.TaskType}-{stamp}");
        try
        {
            SetTrainingState(true);
            ResetChart();
            TrainingStatusText.Text = "正在整理数据集……";
            AddLog($"任务：{TaskTypeText.Text}；模型：{model.DisplayName}");
            AddLog($"训练图像尺寸：{FormatImageSize(selectedImageSize)}；训练输入尺寸：{imageSize}px");
            var dataYaml = await Task.Run(() => AppServices.Instance.Datasets.ExportYolo(
                _dataset, trainingDataDirectory, selectedImageSize));
            AddLog($"训练数据已生成：{dataYaml}");
            if (model.IsExisting) AddLog("将加载已有模型，并在该模型基础上继续学习。");

            _trainingCancellation = new CancellationTokenSource();
            StartButton.Content = "暂停训练";
            StartButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
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
            RemoveCurrentDraftStorage(result.RunDirectory);
            SaveModelNode(new TrainingModelNode
            {
                Name = GetCurrentNodeName(runName),
                DatasetRoot = _dataset.RootDirectory,
                TaskType = _dataset.TaskType,
                BaseModel = model.ModelPath,
                ModelPath = result.ModelPath,
                RunDirectory = result.RunDirectory,
                EnginePath = result.EnginePath,
                CompletedAt = DateTime.Now,
                Epochs = epochs,
                BatchSize = batchSize,
                ImageSize = selectedImageSize,
                Device = device,
                ExportTensorRt = ExportTensorRtCheckBox.IsChecked == true,
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
            if (_isNewModel) CaptureCurrentDraftState();
            _trainingCancellation?.Dispose();
            _trainingCancellation = null;
            SetTrainingState(false);
        }
    }

    private async Task StartAtu5TrainingAsync()
    {
        if (_dataset is null || _selectedModelNode is null || ModelCombo.SelectedItem is not ModelOption model) return;
        var initialWeight = "models/atu5.pt";
        if (!AppServices.Instance.Atu5Training.IsModelAvailable(initialWeight))
        {
            ThemedMessageBox.Show($"ATU5 初始权重不存在：{initialWeight}", "ATU5 训练", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParsePositive(EpochsText.Text, "训练轮数", out var epochs) ||
            !TryGetTrainingImageSize(out var selectedImageSize, out var imageSize)) return;
        if (BatchSizeCombo.SelectedItem is not int batchSize)
        {
            ThemedMessageBox.Show("请选择批大小。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var trainingDataDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "training-data", "atu5-" + stamp);
        var outputDirectory = IoPath.Combine(_dataset.RootDirectory, ".visionworkbench", "models");
        var runName = GetTrainingRunName($"{SanitizeFileName(_dataset.Name)}-semantic_segmentation-{stamp}");
        try
        {
            SetTrainingState(true);
            ResetChart();
            TrainingStatusText.Text = "正在整理语义分割标注…";
            AddLog($"任务：语义分割（ATU5 / FPN）；模型：{model.DisplayName}");
            AddLog($"训练图像尺寸：{FormatImageSize(selectedImageSize)}；训练输入尺寸：{imageSize}px");
            var manifestPath = await Task.Run(() => AppServices.Instance.Datasets.ExportSemantic(
                _dataset, trainingDataDirectory, selectedImageSize));
            AddLog($"语义分割训练清单已生成：{manifestPath}");
            _trainingCancellation = new CancellationTokenSource();
            StartButton.Content = "暂停训练";
            StartButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
            var request = new Atu5TrainingRequest(manifestPath, initialWeight, epochs, batchSize, imageSize,
                device, outputDirectory, runName,
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
            RemoveCurrentDraftStorage(result.RunDirectory);
            SaveModelNode(new TrainingModelNode
            {
                Name = GetCurrentNodeName(IoPath.GetFileName(result.RunDirectory)),
                DatasetRoot = _dataset.RootDirectory,
                TaskType = _dataset.TaskType,
                BaseModel = model.ModelPath,
                ModelPath = result.ModelPath,
                RunDirectory = result.RunDirectory,
                EnginePath = result.EnginePath,
                CompletedAt = DateTime.Now,
                Epochs = epochs,
                BatchSize = batchSize,
                ImageSize = selectedImageSize,
                Device = device,
                ExportTensorRt = ExportTensorRtCheckBox.IsChecked == true,
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
            if (_isNewModel) CaptureCurrentDraftState();
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

    private async void TrainingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_trainingCancellation is not null || AppServices.Instance.Yolo11Training.IsRunning || AppServices.Instance.Atu5Training.IsRunning)
        {
            await ToggleTrainingPauseAsync();
            return;
        }

        await StartTrainingAsync();
    }

    private async Task ToggleTrainingPauseAsync()
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
            StartButton.Content = "继续训练";
            StartButton.IsEnabled = true;
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
        StartButton.Content = "暂停训练";
        StartButton.IsEnabled = true;
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
                var totalEpochs = progress.TotalEpochs > 0 ? progress.TotalEpochs : GetLossChartMaxEpoch();
                var epoch = Math.Clamp(progress.Epoch, 1, Math.Max(1, totalEpochs));
                var existingIndex = _lossPoints.FindIndex(point => point.Epoch == epoch);
                if (existingIndex >= 0)
                    _lossPoints[existingIndex] = (epoch, loss);
                else
                    _lossPoints.Add((epoch, loss));
                CurrentLossText.Text = $"Epoch {epoch}/{totalEpochs}  Loss: {loss:0.0000}";
                DrawLossCurve();
                _ = RefreshGpuMemoryTextAsync();
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
            StartButton.Content = "开始训练";
        }
        StartButton.IsEnabled = !running && CanStartTraining();
        StartButton.ToolTip = !running && _selectedModelNode is null
            ? "请先选择或新建模型节点。"
            : null;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running;
        DatasetCombo.IsEnabled = !running;
        NewModelButton.IsEnabled = !running;
        // 训练期间锁定节点列表的交互，但不要将 ListBox 设为禁用。
        // WPF 的禁用模板会把滚动内容绘制成系统白色背景，破坏当前主题。
        ModelNodeList.IsEnabled = true;
        ModelNodeList.IsHitTestVisible = !running;
        ModelCombo.IsEnabled = !running;
        BatchSizeCombo.IsEnabled = !running;
        ImageSizeCombo.IsEnabled = !running;
        DeviceCombo.IsEnabled = !running;
        SetModelEditorState(!running && _isNewModel);
        if (running) _ = RefreshGpuMemoryTextAsync();
    }

    private bool CanStartTraining() =>
        _selectedModelNode is not null &&
        _isNewModel &&
        _dataset?.TaskType is ("detection" or "instance_segmentation" or "pose" or "semantic_segmentation");

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
        _lossHoverLine = null;
        _lossHoverLabel = null;
        _lossChartHasData = false;
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

        var maxEpoch = GetLossChartMaxEpoch();
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
        _lossChartTop = top;
        _lossChartHeight = plotHeight;
        _lossChartMin = minLoss;
        _lossChartMax = maxLoss;
        _lossChartFormat = axisFormat;
        _lossChartHasData = true;
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

    private void LossCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_lossChartHasData || LossCanvas is null) return;
        var position = e.GetPosition(LossCanvas);
        var plotLeft = 42d;
        var plotRight = Math.Max(plotLeft, LossCanvas.ActualWidth - 12);
        if (position.X < plotLeft || position.X > plotRight
            || position.Y < _lossChartTop || position.Y > _lossChartTop + _lossChartHeight)
        {
            HideLossHover();
            return;
        }

        var maxEpoch = GetLossChartMaxEpoch();
        var epochAtCursor = 1 + (position.X - plotLeft) * Math.Max(1, maxEpoch - 1)
            / Math.Max(1, plotRight - plotLeft);
        var point = _lossPoints
            .Where(item => double.IsFinite(item.Loss))
            .OrderBy(item => Math.Abs(item.Epoch - epochAtCursor))
            .FirstOrDefault();
        if (point == default)
        {
            HideLossHover();
            return;
        }

        var pointX = plotLeft + (point.Epoch - 1) * Math.Max(1, plotRight - plotLeft)
            / Math.Max(1, maxEpoch - 1);
        var value = point.Loss;
        var pointY = _lossChartTop + (_lossChartMax - value) * _lossChartHeight
            / Math.Max(0.000001, _lossChartMax - _lossChartMin);
        _lossHoverLine ??= new Line
        {
            Stroke = ThemeBrush("AccentBrush", Colors.DodgerBlue),
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
            IsHitTestVisible = false,
        };
        if (!_lossHoverLine.IsVisible) LossCanvas.Children.Add(_lossHoverLine);
        _lossHoverLine.X1 = pointX;
        _lossHoverLine.X2 = pointX;
        _lossHoverLine.Y1 = _lossChartTop;
        _lossHoverLine.Y2 = _lossChartTop + _lossChartHeight;

        _lossHoverLabel ??= new TextBlock
        {
            Width = 92,
            Padding = new Thickness(5, 2, 5, 2),
            Background = ThemeBrush("SurfaceAltBrush", Colors.DimGray),
            Foreground = ThemeBrush("TextBrush", Colors.White),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
        if (!_lossHoverLabel.IsVisible) LossCanvas.Children.Add(_lossHoverLabel);
        _lossHoverLabel.Text = $"Epoch {point.Epoch}\nLoss: {value.ToString(_lossChartFormat, CultureInfo.InvariantCulture)}";
        var labelLeft = pointX - _lossHoverLabel.Width / 2;
        var labelTop = pointY - 42;
        if (labelTop < _lossChartTop) labelTop = pointY + 8;
        Canvas.SetLeft(_lossHoverLabel, Math.Clamp(labelLeft, 0, Math.Max(0, LossCanvas.ActualWidth - _lossHoverLabel.Width)));
        Canvas.SetTop(_lossHoverLabel, Math.Clamp(labelTop, 0, Math.Max(0, LossCanvas.ActualHeight - 42)));
    }

    private int GetLossChartMaxEpoch()
    {
        if (_selectedModelNode?.Epochs > 0) return _selectedModelNode.Epochs;
        if (int.TryParse(EpochsText.Text, out var configured) && configured > 0) return configured;
        return Math.Max(1, _lossPoints.Count == 0 ? 1 : _lossPoints.Max(point => point.Epoch));
    }

    private void LossCanvas_MouseLeave(object sender, MouseEventArgs e) => HideLossHover();

    private void HideLossHover()
    {
        if (_lossHoverLine is not null) LossCanvas.Children.Remove(_lossHoverLine);
        if (_lossHoverLabel is not null) LossCanvas.Children.Remove(_lossHoverLabel);
        _lossHoverLine = null;
        _lossHoverLabel = null;
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
        public bool ExportTensorRt { get; set; }
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
