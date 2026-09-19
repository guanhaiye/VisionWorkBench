using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VisionWorkbench.Application;
using IoPath = System.IO.Path;

namespace VisionWorkbench.App;

public enum AnnotationPlatform
{
    Detection,
    Segmentation,
    SemanticSegmentation,
    InstanceSegmentation,
}

/// <summary>离线图片数据集标注页面，按目标检测与分割两个平台隔离展示。</summary>
public partial class DatasetAnnotationPage : UserControl
{
    private readonly AnnotationPlatform _platform;
    private DatasetDefinition? _dataset;
    private DatasetImageItem? _image;
    private DatasetAnnotation? _annotation;
    private Point _dragStart;
    private Rectangle? _draft;
    private bool _dragging;
    private bool _sam1ClickMode;
    private bool _yoloeRunning;
    private readonly DispatcherTimer _yoloeSpinnerTimer;
    private int _yoloeSpinnerFrame;
    private bool _manualDrawMode = true;
    private bool _brushMode;
    private bool _brushDragging;
    private double _brushDiameter = 36;
    private Point? _brushCursorPoint;
    private Point _brushLastPoint;
    private bool _brushChanged;
    private readonly List<Point> _brushStrokePoints = [];
    private Polyline? _brushStrokeVisual;
    private Ellipse? _brushCursorVisual;
    private bool _eraserMode;
    private bool _eraserDragging;
    private double _eraserDiameter = 36;
    private Point? _eraserCursorPoint;
    private Point _eraserLastPoint;
    private bool _eraserChanged;
    private bool _refreshingImageList;
    private readonly List<Point> _eraserStrokePoints = [];
    private Polyline? _eraserStrokeVisual;
    private Ellipse? _eraserCursorVisual;
    private readonly List<Sam1Prompt> _sam1Prompts = [];
    private int _sam1ResultIndex = -1;
    private int _sam1PromptVersion;
    private readonly DispatcherTimer _sam1HoverTimer;
    private Point? _sam1HoverPoint;
    private SmartAnnotationObjectResult? _sam1HoverPreview;
    private int _sam1HoverVersion;
    private bool _sam1HoverBusy;
    private bool _showAnnotationInfo = true;
    private bool _showBoundingBoxes = true;
    private bool _showContours = true;
    private double _zoom = 1;
    private readonly List<Point> _polygonPoints = [];
    private Polyline? _polygonDraft;
    private int _editingIndex = -1;
    private EditMode _editMode;
    private Point _editStart;
    private DatasetAnnotationObject? _editOriginal;

    private enum EditMode
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    public DatasetAnnotationPage() : this(AnnotationPlatform.Detection)
    {
    }

    public DatasetAnnotationPage(AnnotationPlatform platform)
    {
        _platform = platform;
        InitializeComponent();
        var showInstanceSegmentationOptions = platform == AnnotationPlatform.InstanceSegmentation;
        InstanceSegmentationOptionsSeparator.Visibility = showInstanceSegmentationOptions
            ? Visibility.Visible
            : Visibility.Collapsed;
        BoundingBoxCheckBox.Visibility = showInstanceSegmentationOptions
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContourCheckBox.Visibility = showInstanceSegmentationOptions
            ? Visibility.Visible
            : Visibility.Collapsed;
        AnnotationScrollViewer.ZoomChanged += (_, zoom) =>
        {
            _zoom = zoom;
            ZoomText.Text = $"缩放：{zoom:P0}";
        };
        PlatformTitleText.Text = platform switch
        {
            AnnotationPlatform.Detection => "目标检测标注平台",
            AnnotationPlatform.SemanticSegmentation => "语义分割标注平台",
            AnnotationPlatform.InstanceSegmentation => "实例分割标注平台",
            _ => "分割标注平台",
        };
        ExportDatasetButton.Content = platform == AnnotationPlatform.Detection
            ? "导出 YOLO 检测数据集"
            : "导出分割数据集";
        YoloeButton.Visibility = platform == AnnotationPlatform.Detection
            ? Visibility.Visible
            : Visibility.Collapsed;
        Sam1Button.Visibility = IsSegmentationPlatform
            ? Visibility.Visible
            : Visibility.Collapsed;
        _yoloeSpinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _yoloeSpinnerTimer.Tick += (_, _) =>
        {
            if (!_yoloeRunning) return;
            var frames = new[] { "⟳", "◴", "◷", "◶" };
            YoloeButton.Content = $"{frames[_yoloeSpinnerFrame++ % frames.Length]} 处理中...";
        };
        // Submit the latest hover position at roughly one display frame instead of
        // waiting for the pointer to become stationary.
        _sam1HoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _sam1HoverTimer.Tick += Sam1HoverTimer_Tick;
        Loaded += (_, _) =>
        {
            PanelOption_Changed(this, new RoutedEventArgs());
            RefreshDatasets();
        };
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) RefreshDatasets(_dataset?.Id);
        };
    }

    private void PanelOption_Changed(object sender, RoutedEventArgs e)
    {
        if (DatasetInfoPanel is null || AnnotationToolsPanel is null ||
            AnnotationInfoHeaderRow is null || AnnotationInfoListRow is null ||
            AnnotationInfoCheckBox is null || BoundingBoxCheckBox is null || ContourCheckBox is null)
        {
            return;
        }

        DatasetInfoPanel.Visibility = Visibility.Visible;
        AnnotationToolsPanel.Visibility = Visibility.Visible;

        _showAnnotationInfo = AnnotationInfoCheckBox.IsChecked == true;
        _showBoundingBoxes = BoundingBoxCheckBox.IsChecked == true;
        _showContours = ContourCheckBox.IsChecked == true;
        AnnotationInfoHeaderRow.Height = GridLength.Auto;
        AnnotationInfoListRow.Height = new GridLength(1, GridUnitType.Star);
        RenderAnnotations();
    }

    private void RefreshDatasets(string? selectId = null)
    {
        var datasets = VisibleDatasets();
        DatasetList.ItemsSource = datasets;
        if (selectId is not null)
        {
            DatasetList.SelectedItem = datasets.FirstOrDefault(x => x.Id == selectId);
        }
        if (DatasetList.SelectedItem is null && datasets.Count > 0)
        {
            DatasetList.SelectedIndex = 0;
        }
    }

    private void NewDataset_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        string taskType;
        if (_platform == AnnotationPlatform.Detection)
        {
            taskType = "detection";
        }
        else if (_platform == AnnotationPlatform.SemanticSegmentation)
        {
            taskType = "semantic_segmentation";
        }
        else if (_platform == AnnotationPlatform.InstanceSegmentation)
        {
            taskType = "instance_segmentation";
        }
        else
        {
            var typeDialog = new DatasetTypeDialog(segmentationOnly: true) { Owner = Window.GetWindow(this) };
            if (typeDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(typeDialog.SelectedTaskType)) return;
            taskType = typeDialog.SelectedTaskType;
        }
        _dataset = new DatasetDefinition
        {
            Name = $"dataset-{DateTime.Now:MMddHHmmss}",
            TaskType = taskType,
            Classes = ["object"],
        };
        DatasetNameText.Text = _dataset.Name;
        SourceRootText.Text = "";
        DatasetRootText.Text = "";
        DatasetClassesText.Text = "object";
        ClassCombo.ItemsSource = _dataset.Classes;
        ClassCombo.SelectedIndex = 0;
        UpdateTaskTypeUi();
        ImageList.ItemsSource = null;
        UpdateImageListEmptyState();
        ClearImageView();
        var datasets = VisibleDatasets();
        datasets.Add(_dataset);
        DatasetList.ItemsSource = datasets;
        DatasetList.SelectedItem = _dataset;
        StatusText.Text = "已新建数据集单元。请填写图片目录和类别，然后保存数据集。";
    }

    private void LoadDataset_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        var dialog = new OpenFolderDialog { Title = "加载已有数据集目录" };
        if (dialog.ShowDialog() != true) return;
        var rootDirectory = dialog.FolderName;
        var existing = AppServices.Instance.Datasets.List()
            .FirstOrDefault(dataset => PathsEqual(dataset.RootDirectory, rootDirectory));
        if (existing is not null)
        {
            if (!IsDatasetForCurrentPlatform(existing))
            {
                ShowWrongPlatform(existing.TaskType);
                return;
            }
            RefreshDatasets(existing.Id);
            StatusText.Text = $"数据集已在列表中：{existing.Name}";
            return;
        }

        try
        {
            var directory = new DirectoryInfo(rootDirectory);
            var inferred = InferDatasetMetadata(rootDirectory);
            if (!IsTaskTypeForCurrentPlatform(inferred.TaskType))
            {
                ShowWrongPlatform(inferred.TaskType);
                return;
            }
            var dataset = AppServices.Instance.Datasets.Save(new DatasetDefinition
            {
                Name = directory.Name,
                RootDirectory = rootDirectory,
                TaskType = inferred.TaskType,
                Classes = inferred.Classes.Count > 0 ? inferred.Classes : ["object"],
            });
            RefreshDatasets(dataset.Id);
            var taskName = inferred.TaskType switch
            {
                "semantic_segmentation" => "语义分割",
                "instance_segmentation" => "实例分割",
                _ => "目标检测",
            };
            StatusText.Text = $"已加载数据集：{dataset.Name}。已根据标注自动识别为{taskName}，类别：{string.Join("、", dataset.Classes)}。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"加载数据集失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static (string TaskType, List<string> Classes) InferDatasetMetadata(string rootDirectory)
    {
        var probe = new DatasetDefinition { RootDirectory = rootDirectory };
        var images = AppServices.Instance.Datasets.ListImages(probe);
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasPolygon = false;
        var hasPose = false;
        foreach (var image in images)
        {
            var annotation = AppServices.Instance.Datasets.LoadAnnotation(probe, image.RelativePath);
            foreach (var item in annotation.Objects)
            {
                if (!string.IsNullOrWhiteSpace(item.ClassName)) classes.Add(item.ClassName.Trim());
                if (item.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && item.Polygon.Count >= 3)
                    hasPolygon = true;
                if (item.Shape.Equals("pose", StringComparison.OrdinalIgnoreCase) && item.Keypoints.Count > 0)
                    hasPose = true;
            }
        }

        var semanticMaskDirectory = Directory.Exists(System.IO.Path.Combine(rootDirectory, "masks")) ||
                                    Directory.Exists(System.IO.Path.Combine(rootDirectory, ".visionworkbench", "masks"));
        var taskType = hasPose
            ? "pose"
            : semanticMaskDirectory
            ? "semantic_segmentation"
            : hasPolygon
                ? "instance_segmentation"
                : "detection";
        return (taskType, classes.ToList());
    }

    private bool IsDatasetForCurrentPlatform(DatasetDefinition dataset) =>
        IsTaskTypeForCurrentPlatform(dataset.TaskType);

    private List<DatasetDefinition> VisibleDatasets() =>
        AppServices.Instance.Datasets.List()
            .Where(IsDatasetForCurrentPlatform)
            .ToList();

    private bool IsTaskTypeForCurrentPlatform(string? taskType) => _platform switch
    {
        AnnotationPlatform.Detection => taskType == "detection",
        AnnotationPlatform.Segmentation => taskType is "semantic_segmentation" or "instance_segmentation",
        AnnotationPlatform.SemanticSegmentation => taskType == "semantic_segmentation",
        AnnotationPlatform.InstanceSegmentation => taskType == "instance_segmentation",
        _ => false,
    };

    private bool IsSegmentationPlatform => _platform is AnnotationPlatform.Segmentation or AnnotationPlatform.SemanticSegmentation or AnnotationPlatform.InstanceSegmentation;

    private void ShowWrongPlatform(string? taskType)
    {
        var target = taskType switch
        {
            "detection" => "目标检测标注平台",
            "semantic_segmentation" => "语义分割标注平台",
            "instance_segmentation" => "实例分割标注平台",
            _ => "对应标注平台",
        };
        ThemedMessageBox.Show($"该数据集属于{target}，请从左侧进入对应平台后再加载。",
            "标注平台不匹配", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        var dialog = new OpenFolderDialog { Title = "选择图片数据集目录" };
        if (Directory.Exists(SourceRootText.Text)) dialog.InitialDirectory = SourceRootText.Text;
        if (dialog.ShowDialog() == true)
        {
            try
            {
                if (_dataset is null)
                {
                    NewDataset_Click(sender, e);
                    if (_dataset is null) return;
                }
                var sourceDirectory = IoPath.GetFullPath(dialog.FolderName);
                var datasetDirectory = CreateDatasetDirectory(sourceDirectory, _dataset!.Name);
                CopyImagesToDataset(sourceDirectory, datasetDirectory);
                SourceRootText.Text = sourceDirectory;
                DatasetRootText.Text = datasetDirectory;
                _dataset!.RootDirectory = datasetDirectory;
                _dataset.Name = DatasetNameText.Text.Trim();
                _dataset.Classes = ParseClasses(DatasetClassesText.Text);
                _dataset = AppServices.Instance.Datasets.Save(_dataset);
                RefreshDatasets(_dataset.Id);
                var images = AppServices.Instance.Datasets.ListImages(_dataset);
                ImageList.ItemsSource = images;
                UpdateImageListEmptyState();
                ImageList.SelectedIndex = images.Count > 0 ? 0 : -1;
                StatusText.Text = $"已读取 {sourceDirectory}，图片已复制到独立数据集目录，共 {images.Count} 张。原始目录未修改。";
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"创建独立数据集失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private static string CreateDatasetDirectory(string sourceDirectory, string datasetName)
    {
        var storageRoot = AppServices.Instance.Settings.DatasetDirectory;
        if (string.IsNullOrWhiteSpace(storageRoot)) storageRoot = @"E:\";
        storageRoot = IoPath.GetFullPath(storageRoot.Trim());
        Directory.CreateDirectory(storageRoot);

        var sourceName = new DirectoryInfo(sourceDirectory).Name;
        var safeName = string.IsNullOrWhiteSpace(datasetName) ? sourceName : datasetName.Trim();
        foreach (var invalid in IoPath.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "dataset";

        var candidate = IoPath.Combine(storageRoot, safeName);
        if (PathsEqual(candidate, sourceDirectory))
        {
            candidate = IoPath.Combine(storageRoot, safeName + "-visionworkbench");
        }
        if (!Directory.Exists(candidate)) return candidate;

        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var withTimestamp = IoPath.Combine(storageRoot, $"{safeName}-{suffix}");
        return Directory.Exists(withTimestamp)
            ? IoPath.Combine(storageRoot, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss-fff}")
            : withTimestamp;
    }

    private static void CopyImagesToDataset(string sourceDirectory, string datasetDirectory)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp" };
        var files = Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(IoPath.GetExtension(path)))
            .Where(path => !IsInternalDatasetPath(IoPath.GetRelativePath(sourceDirectory, path)))
            .ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException("原始目录中没有找到 jpg、jpeg、png 或 bmp 图片。");

        Directory.CreateDirectory(datasetDirectory);
        foreach (var sourcePath in files)
        {
            var relativePath = IoPath.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = IoPath.Combine(datasetDirectory, relativePath);
            Directory.CreateDirectory(IoPath.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static bool IsInternalDatasetPath(string relativePath)
    {
        var firstSegment = relativePath
            .Split([IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.Equals(firstSegment, ".visionworkbench", StringComparison.OrdinalIgnoreCase);
    }

    private void SaveDataset_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        try
        {
            _dataset ??= new DatasetDefinition();
            _dataset.Name = DatasetNameText.Text.Trim();
            _dataset.RootDirectory = DatasetRootText.Text.Trim();
            _dataset.Classes = ParseClasses(DatasetClassesText.Text);
            _dataset = AppServices.Instance.Datasets.Save(_dataset);
            RefreshDatasets(_dataset.Id);
            StatusText.Text = $"数据集已保存，共 {AppServices.Instance.Datasets.ListImages(_dataset).Count} 张图片。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"保存数据集失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoSplit_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        if (_dataset is null)
        {
            ThemedMessageBox.Show("请先选择或保存数据集。", "数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new DatasetSplitDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var selectedPath = _image?.RelativePath;
            var beforeSplit = AppServices.Instance.Datasets.ListImages(_dataset);
            var annotatedCount = beforeSplit.Count(image => image.HasAnnotation);
            if (annotatedCount == 0)
            {
                ThemedMessageBox.Show("当前数据集没有已标注图片，未标注图片不会参与自动划分。", "数据集划分",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _dataset = AppServices.Instance.Datasets.AutoSplit(_dataset, dialog.TrainRatio);
            RefreshImageList(selectedPath);
            var images = AppServices.Instance.Datasets.ListImages(_dataset);
            var train = images.Count(x => x.Split == "train");
            var validation = images.Count(x => x.Split == "val");
            var skipped = beforeSplit.Count - annotatedCount;
            StatusText.Text = $"已自动划分数据集：训练集 {train} 张，验证集 {validation} 张（比例 {(dialog.TrainRatio * 100):0}%:{(100 - dialog.TrainRatio * 100):0}%）。已跳过 {skipped} 张未标注图片，保持未划分。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"自动划分失败：{ex.Message}", "数据集划分", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) { ThemedMessageBox.Show("请先选择或保存数据集。", "数据集"); return; }
        var dialog = new OpenFolderDialog { Title = "选择 YOLO 数据集导出目录" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var output = AppServices.Instance.Datasets.ExportYolo(_dataset, dialog.FolderName);
            var exportType = _dataset.TaskType switch
            {
                "instance_segmentation" => "YOLO11-seg 实例分割",
                "semantic_segmentation" => "多边形分割",
                _ => "YOLO 目标检测",
            };
            StatusText.Text = $"{exportType}数据集已导出：{output}（包含 data.yaml、images 和 labels）";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"导出失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImageList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = FindImageListIndex(e.OriginalSource as DependencyObject);
        if (index is null) return;
        ImageList.SelectedIndex = index.Value;
        if (ImageList.SelectedItem is DatasetImageItem image)
        {
            ShowImageContextMenu(image);
            e.Handled = true;
        }
    }

    private int? FindImageListIndex(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return source is ListBoxItem item
            ? ImageList.ItemContainerGenerator.IndexFromContainer(item)
            : null;
    }

    private void ShowImageContextMenu(DatasetImageItem image)
    {
        if (_dataset is null) return;
        var menu = new ContextMenu { PlacementTarget = ImageList };
        var train = new MenuItem { Header = "切换为训练集" };
        train.Click += (_, _) => ChangeImageSplit(image.RelativePath, "train");
        menu.Items.Add(train);
        var validation = new MenuItem { Header = "切换为验证集" };
        validation.Click += (_, _) => ChangeImageSplit(image.RelativePath, "val");
        menu.Items.Add(validation);
        var unassigned = new MenuItem { Header = "设为未划分" };
        unassigned.Click += (_, _) => ChangeImageSplit(image.RelativePath, "unassigned");
        menu.Items.Add(unassigned);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = $"当前状态：{image.SplitDisplay}", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void ChangeImageSplit(string relativePath, string split)
    {
        if (_dataset is null) return;
        try
        {
            var selectedPath = _image?.RelativePath;
            _dataset = AppServices.Instance.Datasets.SetImageSplit(_dataset, relativePath, split);
            RefreshImageList(selectedPath);
            var display = split switch
            {
                "train" => "训练集",
                "val" => "验证集",
                _ => "未划分",
            };
            StatusText.Text = $"图片 {relativePath} 已切换为{display}。";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"切换图片划分状态失败：{ex.Message}", "数据集划分", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshImageList(string? selectedRelativePath = null, bool suppressSelectionChanged = false)
    {
        if (_dataset is null) return;
        var images = AppServices.Instance.Datasets.ListImages(_dataset);
        if (suppressSelectionChanged) _refreshingImageList = true;
        try
        {
            ImageList.ItemsSource = images;
            UpdateImageListEmptyState();
            if (selectedRelativePath is not null)
            {
                var selected = images.FirstOrDefault(x => x.RelativePath == selectedRelativePath);
                ImageList.SelectedItem = selected;
                if (suppressSelectionChanged && selected is not null) _image = selected;
            }
        }
        finally
        {
            if (suppressSelectionChanged) _refreshingImageList = false;
        }
    }

    private void UpdateImageListEmptyState()
    {
        ImageListEmptyText.Visibility = ImageList.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DatasetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatasetList.SelectedItem is not DatasetDefinition dataset) return;
        _dataset = dataset;
        DatasetNameText.Text = dataset.Name;
        SourceRootText.Text = "当前数据集目录";
        DatasetRootText.Text = dataset.RootDirectory;
        DatasetClassesText.Text = string.Join(", ", dataset.Classes);
        ClassCombo.ItemsSource = dataset.Classes;
        SelectDefaultAnnotationClass();
        UpdateTaskTypeUi();
        ImageList.ItemsSource = AppServices.Instance.Datasets.ListImages(dataset);
        UpdateImageListEmptyState();
        ClearImageView();
        var shapeName = IsPolygonMode() ? "多边形" : "矩形框";
        StatusText.Text = $"{dataset.Name}：{ImageList.Items.Count} 张图片。标注形状为{shapeName}。";
    }

    private void DatasetList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = FindDatasetListIndex(e.OriginalSource as DependencyObject);
        if (index is null) return;
        DatasetList.SelectedIndex = index.Value;
        if (DatasetList.SelectedItem is DatasetDefinition dataset)
        {
            ShowDatasetContextMenu(dataset);
            e.Handled = true;
        }
    }

    private int? FindDatasetListIndex(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source is ListBoxItem item
            ? DatasetList.ItemContainerGenerator.IndexFromContainer(item)
            : null;
    }

    private void ShowDatasetContextMenu(DatasetDefinition dataset)
    {
        var menu = new ContextMenu { PlacementTarget = DatasetList };
        var open = new MenuItem { Header = "打开本地目录" };
        open.Click += (_, _) => OpenDatasetDirectory(dataset);
        menu.Items.Add(open);
        var rename = new MenuItem { Header = "修改数据集名称" };
        rename.Click += (_, _) => RenameDataset(dataset);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        var removeFromList = new MenuItem { Header = "仅从列表中删除（保留本地数据）" };
        removeFromList.Click += (_, _) => DeleteDataset(dataset, deleteLocalData: false);
        menu.Items.Add(removeFromList);
        var deleteLocal = new MenuItem { Header = "彻底删除（包含本地数据）" };
        deleteLocal.Click += (_, _) => DeleteDataset(dataset, deleteLocalData: true);
        menu.Items.Add(deleteLocal);
        menu.IsOpen = true;
    }

    private void OpenDatasetDirectory(DatasetDefinition dataset)
    {
        if (!Directory.Exists(dataset.RootDirectory))
        {
            ThemedMessageBox.Show($"本地目录不存在：{dataset.RootDirectory}", "打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = dataset.RootDirectory,
            UseShellExecute = true,
        });
    }

    private void RenameDataset(DatasetDefinition dataset)
    {
        var dialog = new DatasetRenameDialog(dataset.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            dataset.Name = dialog.DatasetName;
            AppServices.Instance.Datasets.Save(dataset);
            RefreshDatasets(dataset.Id);
            StatusText.Text = $"数据集已重命名为：{dataset.Name}";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"修改数据集名称失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteDataset(DatasetDefinition dataset, bool deleteLocalData)
    {
        var owner = Window.GetWindow(this);
        var localDeleteMode = LocalDeleteMode.RecycleBin;
        if (!deleteLocalData)
        {
            var result = ThemedMessageBox.Show(
                owner,
                $"确定要从列表中移除数据集“{dataset.Name}”吗？\n\n本地图片、标注文件和数据集目录都会保留。",
                "从列表中删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }
        else
        {
            if (!TryChooseLocalDeleteMode(dataset, out var mode)) return;
            localDeleteMode = mode;
            if (mode == LocalDeleteMode.Permanent)
            {
                var result = ThemedMessageBox.Show(
                    owner,
                    $"即将永久删除数据集“{dataset.Name}”及其本地文件，删除后无法从回收站恢复。\n\n目录：{dataset.RootDirectory}\n\n确定继续吗？",
                    "确认永久删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            try
            {
                var root = IoPath.GetFullPath(dataset.RootDirectory.Trim());
                if (!Directory.Exists(root))
                {
                    AppServices.Instance.Datasets.Delete(dataset.Id);
                    StatusText.Text = $"本地目录已不存在，数据集“{dataset.Name}”已从列表中删除。";
                    RefreshAfterDatasetDeletion(dataset);
                    return;
                }

                if (IsUnsafeDatasetRoot(root))
                    throw new InvalidOperationException("为避免误删整块磁盘，不能删除磁盘根目录。请先修改数据集目录。 ");

                if (mode == LocalDeleteMode.RecycleBin)
                    VbFileSystem.DeleteDirectory(root,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(owner, $"删除本地数据失败：{ex.Message}", "删除数据集失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        AppServices.Instance.Datasets.Delete(dataset.Id);
        RefreshAfterDatasetDeletion(dataset);
        StatusText.Text = deleteLocalData
            ? localDeleteMode == LocalDeleteMode.RecycleBin
                ? $"数据集“{dataset.Name}”已删除，本地数据已移动到回收站。"
                : $"数据集“{dataset.Name}”及本地数据已永久删除。"
            : $"数据集“{dataset.Name}”已从列表中删除，本地数据已保留。";
    }

    private enum LocalDeleteMode
    {
        RecycleBin,
        Permanent,
    }

    private bool TryChooseLocalDeleteMode(DatasetDefinition dataset, out LocalDeleteMode mode)
    {
        mode = LocalDeleteMode.RecycleBin;
        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = "彻底删除数据集",
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
        var selectedMode = LocalDeleteMode.RecycleBin;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"确定要彻底删除数据集“{dataset.Name}”吗？",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"这会删除本地图片、标注文件和数据集目录：\n{dataset.RootDirectory}\n\n请选择处理方式：",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("MutedTextBrush"),
            Margin = new Thickness(0, 0, 0, 18),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", MinWidth = 86, IsCancel = true };
        cancel.Style = (Style)System.Windows.Application.Current.FindResource("SecondaryButton");
        cancel.Click += (_, _) => window.Close();
        var recycle = new Button { Content = "移动到回收站", MinWidth = 120, Margin = new Thickness(8, 2, 0, 2) };
        recycle.Click += (_, _) => { selectedMode = LocalDeleteMode.RecycleBin; result = true; window.Close(); };
        var permanent = new Button
        {
            Content = "直接删除",
            MinWidth = 100,
            Margin = new Thickness(8, 2, 0, 2),
            Background = (Brush)System.Windows.Application.Current.FindResource("DangerBrush"),
            BorderBrush = (Brush)System.Windows.Application.Current.FindResource("DangerBrush"),
        };
        permanent.Click += (_, _) => { selectedMode = LocalDeleteMode.Permanent; result = true; window.Close(); };
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
        window.Loaded += (_, _) => recycle.Focus();
        window.ShowDialog();
        mode = selectedMode;
        return result;
    }

    private static bool IsUnsafeDatasetRoot(string root)
    {
        var driveRoot = IoPath.GetPathRoot(root);
        return string.IsNullOrWhiteSpace(driveRoot)
            || string.Equals(root.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar),
                driveRoot.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshAfterDatasetDeletion(DatasetDefinition dataset)
    {
        if (string.Equals(_dataset?.Id, dataset.Id, StringComparison.OrdinalIgnoreCase))
            _dataset = null;
        RefreshDatasets();
        if (DatasetList.Items.Count == 0)
        {
            _dataset = null;
            DatasetNameText.Text = "";
            DatasetRootText.Text = "";
            DatasetClassesText.Text = "";
            ClassCombo.ItemsSource = null;
            UpdateTaskTypeUi();
            ClearImageView();
        }
    }

    private async void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingImageList) return;
        if (_dataset is null || ImageList.SelectedItem is not DatasetImageItem image) return;
        var keepSam1Active = _sam1ClickMode;
        ResetSam1ImageState();
        ResetZoom();
        _image = image;
        _annotation = AppServices.Instance.Datasets.LoadAnnotation(_dataset, image.RelativePath);
        RefreshImageList(image.RelativePath, suppressSelectionChanged: true);
        AnnotationList.ItemsSource = _annotation.Objects;
        AnnotationList.SelectedIndex = -1;
        try
        {
            AnnotationImage.Source = await Task.Run(() => LoadBitmap(image.FullPath));
            ResetZoom();
            RenderAnnotations();
            if (keepSam1Active)
            {
                _sam1ClickMode = true;
                Sam1Button.Content = "完成智能标注";
                StatusText.Text = $"已切换图片：{image.RelativePath}。智能标注仍处于开启状态，移动鼠标即可预览。";
                if (AnnotationCanvas.IsMouseOver)
                    ScheduleSam1Hover(Mouse.GetPosition(AnnotationCanvas));
            }
            else
            {
                StatusText.Text = $"当前图片：{image.RelativePath}，{_annotation.Objects.Count} 个标注。可在图片上绘制{(IsPolygonMode() ? "多边形" : "矩形框")}。";
            }
        }
        catch (Exception ex)
        {
            ClearImageView();
            StatusText.Text = $"图片加载失败：{ex.Message}";
        }
    }

    private void AnnotationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var canChooseClassAfterDrawing = IsPolygonMode() && (_manualDrawMode || _brushMode);
        if (_dataset is null || _image is null || _annotation is null ||
            (!canChooseClassAfterDrawing && ClassCombo.SelectedItem is not string))
        {
            StatusText.Text = "请选择数据集、图片和标注类别。";
            return;
        }
        if (_sam1ClickMode)
        {
            e.Handled = true;
            _ = AddSam1PromptAsync(e.GetPosition(AnnotationCanvas), label: 1);
            return;
        }
        var point = e.GetPosition(AnnotationCanvas);
        if (_brushMode && IsPolygonMode())
        {
            if (!TryGetImageNormalizedPoint(point, GetImageRect(), out _)) return;
            _brushDragging = true;
            _brushChanged = false;
            _brushLastPoint = point;
            _brushCursorPoint = point;
            _brushStrokePoints.Clear();
            _brushStrokePoints.Add(point);
            AnnotationCanvas.CaptureMouse();
            UpdateBrushVisuals();
            e.Handled = true;
            return;
        }
        if (_eraserMode && IsPolygonMode())
        {
            _eraserDragging = true;
            _eraserChanged = false;
            _eraserLastPoint = point;
            _eraserCursorPoint = point;
            _eraserStrokePoints.Clear();
            _eraserStrokePoints.Add(point);
            AnnotationCanvas.CaptureMouse();
            UpdateEraserVisuals();
            e.Handled = true;
            return;
        }
        if (_manualDrawMode && IsPolygonMode())
        {
            if (!TryGetImageNormalizedPoint(point, GetImageRect(), out _)) return;
            _polygonPoints.Add(point);
            if (e.ClickCount >= 2 && _polygonPoints.Count >= 3)
            {
                _polygonPoints.RemoveAt(_polygonPoints.Count - 1);
                FinishPolygon();
            }
            else
            {
                RenderPolygonDraft();
            }
            e.Handled = true;
            return;
        }
        var hitIndex = HitTestAnnotation(point);
        if (hitIndex >= 0)
        {
            AnnotationList.SelectedIndex = hitIndex;
            var hitObject = _annotation.Objects[hitIndex];
            if (!hitObject.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase))
            {
                _editingIndex = hitIndex;
                _editStart = point;
                _editOriginal = CloneAnnotationObject(hitObject);
                _editMode = GetEditMode(point, hitObject);
                _dragging = true;
                AnnotationCanvas.CaptureMouse();
            }
            e.Handled = true;
            return;
        }
        if (IsPolygonMode()) return;
        if (!TryGetImageNormalizedPoint(point, GetImageRect(), out _)) return;
        _dragging = true;
        _dragStart = point;
        _draft = new Rectangle { Stroke = Brushes.Yellow, StrokeThickness = OverlayStrokeThickness(1.1), StrokeDashArray = [4, 2] };
        AnnotationCanvas.Children.Add(_draft);
        AnnotationCanvas.CaptureMouse();
    }

    private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_brushMode)
        {
            var point = ClampPointToRect(e.GetPosition(AnnotationCanvas), GetImageRect());
            _brushCursorPoint = point;
            if (_brushDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                if ((point - _brushLastPoint).Length >= 2)
                {
                    _brushStrokePoints.Add(point);
                    _brushLastPoint = point;
                }
            }
            UpdateBrushVisuals();
            return;
        }
        if (_eraserMode)
        {
            var point = e.GetPosition(AnnotationCanvas);
            _eraserCursorPoint = point;
            if (_eraserDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                if ((point - _eraserLastPoint).Length >= 2)
                {
                    _eraserStrokePoints.Add(point);
                    _eraserLastPoint = point;
                }
                UpdateEraserVisuals();
            }
            else
            {
                UpdateEraserVisuals();
            }
            return;
        }
        if (_sam1ClickMode && _sam1Prompts.Count == 0)
        {
            ScheduleSam1Hover(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_editingIndex >= 0 && _annotation is not null)
        {
            UpdateEditedAnnotation(e.GetPosition(AnnotationCanvas));
            RenderAnnotations();
            return;
        }
        if (IsPolygonMode() && _polygonPoints.Count > 0)
        {
            RenderPolygonDraft(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (!_dragging || _draft is null) return;
        var imageRect = GetImageRect();
        UpdateRectangle(_draft, _dragStart, ClampPointToRect(e.GetPosition(AnnotationCanvas), imageRect));
    }

    private void AnnotationCanvas_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_brushMode)
        {
            _brushCursorPoint = ClampPointToRect(e.GetPosition(AnnotationCanvas), GetImageRect());
            RenderAnnotations();
            return;
        }
        if (_eraserMode)
        {
            _eraserCursorPoint = e.GetPosition(AnnotationCanvas);
            RenderAnnotations();
            return;
        }
        if (_sam1ClickMode && _sam1Prompts.Count == 0)
            ScheduleSam1Hover(e.GetPosition(AnnotationCanvas));
    }

    private void AnnotationCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_brushMode)
        {
            _brushCursorPoint = null;
            RenderAnnotations();
            return;
        }
        if (_eraserMode)
        {
            _eraserCursorPoint = null;
            RenderAnnotations();
            return;
        }
        if (!_sam1ClickMode || _sam1Prompts.Count > 0) return;
        _sam1HoverTimer.Stop();
        _sam1HoverPoint = null;
        _sam1HoverPreview = null;
        _sam1HoverVersion++;
        RenderAnnotations();
    }

    private void AnnotationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_brushMode && _brushDragging)
        {
            _brushDragging = false;
            AnnotationCanvas.ReleaseMouseCapture();
            PaintStroke(_brushStrokePoints);
            _brushStrokePoints.Clear();
            if (_brushChanged) AutoSaveAnnotation("画刷标注");
            RenderAnnotations();
            e.Handled = true;
            return;
        }
        if (_eraserMode && _eraserDragging)
        {
            _eraserDragging = false;
            AnnotationCanvas.ReleaseMouseCapture();
            EraseStroke(_eraserStrokePoints);
            _eraserStrokePoints.Clear();
            if (_eraserChanged) AutoSaveAnnotation("橡皮擦轨迹");
            RenderAnnotations();
            e.Handled = true;
            return;
        }
        if (_editingIndex >= 0)
        {
            _dragging = false;
            AnnotationCanvas.ReleaseMouseCapture();
            _editingIndex = -1;
            _editMode = EditMode.None;
            _editOriginal = null;
            AutoSaveAnnotation("修改标注");
            e.Handled = true;
            return;
        }
        if (IsPolygonMode()) return;
        if (!_dragging || _draft is null || _annotation is null || ClassCombo.SelectedItem is not string className)
            return;
        var end = ClampPointToRect(e.GetPosition(AnnotationCanvas), GetImageRect());
        var left = Math.Min(_dragStart.X, end.X);
        var top = Math.Min(_dragStart.Y, end.Y);
        var width = Math.Abs(end.X - _dragStart.X);
        var height = Math.Abs(end.Y - _dragStart.Y);
        _draft = null;
        _dragging = false;
        AnnotationCanvas.ReleaseMouseCapture();
        if (width < 4 || height < 4)
        {
            RenderAnnotations();
            return;
        }
        var imageRect = GetImageRect();
        _annotation.Objects.Add(new DatasetAnnotationObject
        {
            ClassName = className,
            Shape = "bbox",
            X = Math.Clamp((left - imageRect.Left) / imageRect.Width, 0, 1),
            Y = Math.Clamp((top - imageRect.Top) / imageRect.Height, 0, 1),
            Width = Math.Clamp(width / imageRect.Width, 0, 1),
            Height = Math.Clamp(height / imageRect.Height, 0, 1),
        });
        AnnotationList.ItemsSource = null;
        AnnotationList.ItemsSource = _annotation.Objects;
        AnnotationList.SelectedIndex = _annotation.Objects.Count - 1;
        RenderAnnotations();
        AutoSaveAnnotation("新增标注");
    }

    private void AnnotationCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(AnnotationCanvas);
        if (_sam1ClickMode && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            _ = AddSam1PromptAsync(point, label: 0);
            return;
        }
        var index = HitTestAnnotation(point);
        if (index >= 0) AnnotationList.SelectedIndex = index;
        ShowAnnotationContextMenu(index >= 0 ? index : null, AnnotationCanvas, _sam1ClickMode ? point : null);
        e.Handled = true;
    }

    private void AnnotationList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = FindAnnotationListIndex(e.OriginalSource as DependencyObject);
        if (index is not null) AnnotationList.SelectedIndex = index.Value;
    }

    private void AnnotationList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = FindAnnotationListIndex(e.OriginalSource as DependencyObject);
        if (index is null) return;
        AnnotationList.SelectedIndex = index.Value;
        ShowAnnotationContextMenu(index.Value, AnnotationList);
        e.Handled = true;
    }

    private int? FindAnnotationListIndex(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source is ListBoxItem item
            ? AnnotationList.ItemContainerGenerator.IndexFromContainer(item)
            : null;
    }

    private void ShowAnnotationContextMenu(int? index, FrameworkElement placementTarget, Point? sam1NegativePoint = null)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        var fit = new MenuItem { Header = "图像自适应窗口" };
        fit.Click += FitImageToWindow_Click;
        menu.Items.Add(fit);

        if (_sam1ClickMode && sam1NegativePoint is { } point)
        {
            var addNegativePoint = new MenuItem { Header = "添加排除点" };
            addNegativePoint.Click += (_, _) => _ = AddSam1PromptAsync(point, label: 0);
            menu.Items.Add(addNegativePoint);
        }

        if (_dataset is not null && _annotation is not null &&
            index is >= 0 && index.Value < _annotation.Objects.Count)
        {
            menu.Items.Add(new Separator());
            foreach (var className in _dataset.Classes)
            {
                var item = new MenuItem { Header = $"切换类别：{className}", Tag = className };
                item.Click += ChangeAnnotationClass_Click;
                menu.Items.Add(item);
            }
            if (_dataset.Classes.Count > 0) menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "删除标注", Tag = index.Value };
            delete.Click += DeleteAnnotationMenu_Click;
            menu.Items.Add(delete);
        }
        menu.IsOpen = true;
    }

    private void FitImageToWindow_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom();
        StatusText.Text = "图像已自适应标注窗口。";
    }

    private void ChangeAnnotationClass_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation is null || sender is not MenuItem item || item.Tag is not string className) return;
        var index = AnnotationList.SelectedIndex;
        if (index < 0 || index >= _annotation.Objects.Count) return;
        _annotation.Objects[index].ClassName = className;
        RefreshAnnotationList(index);
        AutoSaveAnnotation("切换类别");
    }

    private void DeleteAnnotationMenu_Click(object sender, RoutedEventArgs e) => DeleteAnnotation_Click(sender, e);

    private void FinishPolygon_Click(object sender, RoutedEventArgs e) => FinishPolygon();

    private void SelectDefaultAnnotationClass()
    {
        if (ClassCombo.Items.Count == 0) return;
        var defaultClass = ClassCombo.Items
            .OfType<string>()
            .FirstOrDefault(value => string.Equals(value, "object", StringComparison.OrdinalIgnoreCase));
        ClassCombo.SelectedItem = defaultClass ?? ClassCombo.Items[0];
    }

    private bool TryResolveAnnotationClass(out string className)
    {
        if (PromptClassAfterDrawCheckBox.IsChecked != true && ClassCombo.SelectedItem is string selected)
        {
            className = selected.Trim();
            return !string.IsNullOrWhiteSpace(className);
        }
        return TrySelectAnnotationClass(out className);
    }

    private bool TrySelectAnnotationClass(out string className)
    {
        className = string.Empty;
        if (_dataset is null) return false;

        var dialog = new AnnotationClassDialog(_dataset.Classes)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedClassName))
            return false;

        var selectedClassName = dialog.SelectedClassName.Trim();
        className = selectedClassName;
        if (!_dataset.Classes.Contains(className, StringComparer.OrdinalIgnoreCase))
        {
            _dataset.Classes.Add(className);
            DatasetClassesText.Text = string.Join(", ", _dataset.Classes);
            ClassCombo.ItemsSource = null;
            ClassCombo.ItemsSource = _dataset.Classes;
        }
        ClassCombo.SelectedItem = _dataset.Classes.FirstOrDefault(
            value => string.Equals(value, selectedClassName, StringComparison.OrdinalIgnoreCase));

        try
        {
            _dataset = AppServices.Instance.Datasets.Save(_dataset);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(Window.GetWindow(this), $"保存类别失败：{ex.Message}", "标注类别",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void FinishPolygon()
    {
        if (_annotation is null || _polygonPoints.Count < 3)
        {
            return;
        }
        if (!TryResolveAnnotationClass(out var className))
        {
            CancelPolygonDraft();
            RenderAnnotations();
            StatusText.Text = "已取消本次外轮廓绘制。";
            return;
        }
        var imageRect = GetImageRect();
        var points = _polygonPoints.Select(point => new DatasetPoint
        {
            X = Math.Clamp((point.X - imageRect.Left) / imageRect.Width, 0, 1),
            Y = Math.Clamp((point.Y - imageRect.Top) / imageRect.Height, 0, 1),
        }).ToList();
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        _annotation.Objects.Add(new DatasetAnnotationObject
        {
            ClassName = className,
            Shape = "polygon",
            X = minX,
            Y = minY,
            Width = maxX - minX,
            Height = maxY - minY,
            Polygon = points,
        });
        _polygonPoints.Clear();
        _polygonDraft = null;
        AnnotationList.ItemsSource = null;
        AnnotationList.ItemsSource = _annotation.Objects;
        AnnotationList.SelectedIndex = _annotation.Objects.Count - 1;
        RenderAnnotations();
        AutoSaveAnnotation("新增多边形标注");
    }

    private void RenderPolygonDraft(Point? cursor = null)
    {
        if (_polygonDraft is null)
        {
            _polygonDraft = new Polyline
            {
                Stroke = Brushes.Yellow,
                StrokeThickness = OverlayStrokeThickness(1.1),
                StrokeDashArray = [4, 2],
                IsHitTestVisible = false,
            };
            AnnotationCanvas.Children.Add(_polygonDraft);
        }
        var points = _polygonPoints.ToList();
        if (cursor is { } current) points.Add(current);
        _polygonDraft.Points = new PointCollection(points);
    }

    private void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation is null || AnnotationList.SelectedIndex < 0) return;
        _annotation.Objects.RemoveAt(AnnotationList.SelectedIndex);
        AnnotationList.ItemsSource = null;
        AnnotationList.ItemsSource = _annotation.Objects;
        RenderAnnotations();
        AutoSaveAnnotation("删除标注");
    }

    private void EraseStroke(IReadOnlyList<Point> stroke)
    {
        if (_annotation is null || stroke.Count == 0 || AnnotationCanvas.ActualWidth < 1 || AnnotationCanvas.ActualHeight < 1) return;
        var imageRect = GetImageRect();
        var rasterWidth = Math.Clamp((int)Math.Round(imageRect.Width), 64, 2048);
        var rasterHeight = Math.Clamp((int)Math.Round(imageRect.Height), 64, 2048);
        var rasterStroke = stroke.Select(point => new OpenCvSharp.Point(
            Math.Clamp((int)Math.Round((point.X - imageRect.Left) / imageRect.Width * rasterWidth), 0, rasterWidth - 1),
            Math.Clamp((int)Math.Round((point.Y - imageRect.Top) / imageRect.Height * rasterHeight), 0, rasterHeight - 1))).ToArray();
        var thickness = Math.Max(2, (int)Math.Round(_eraserDiameter / imageRect.Width * rasterWidth));

        var changed = false;
        for (var index = _annotation.Objects.Count - 1; index >= 0; index--)
        {
            var source = _annotation.Objects[index];
            if (!source.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) || source.Polygon.Count < 3) continue;
            using var mask = new OpenCvSharp.Mat(rasterHeight, rasterWidth, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.Black);
            var polygon = source.Polygon.Select(point => new OpenCvSharp.Point(
                Math.Clamp((int)Math.Round(point.X * (rasterWidth - 1)), 0, rasterWidth - 1),
                Math.Clamp((int)Math.Round(point.Y * (rasterHeight - 1)), 0, rasterHeight - 1))).ToArray();
            OpenCvSharp.Cv2.FillPoly(mask, [polygon], OpenCvSharp.Scalar.White);
            var areaBefore = OpenCvSharp.Cv2.CountNonZero(mask);
            if (rasterStroke.Length == 1)
            {
                OpenCvSharp.Cv2.Circle(mask, rasterStroke[0], Math.Max(1, thickness / 2), OpenCvSharp.Scalar.Black, -1);
            }
            else
            {
                OpenCvSharp.Cv2.Polylines(mask, [rasterStroke], false, OpenCvSharp.Scalar.Black,
                    thickness, OpenCvSharp.LineTypes.AntiAlias);
                OpenCvSharp.Cv2.Circle(mask, rasterStroke[0], Math.Max(1, thickness / 2), OpenCvSharp.Scalar.Black, -1);
                OpenCvSharp.Cv2.Circle(mask, rasterStroke[^1], Math.Max(1, thickness / 2), OpenCvSharp.Scalar.Black, -1);
            }
            var areaAfter = OpenCvSharp.Cv2.CountNonZero(mask);
            if (areaAfter == areaBefore) continue;

            // YOLO 分割标注只保存单一外轮廓，不能直接保存多边形内部的“洞”。
            // 如果橡皮擦完全落在区域内部，External 轮廓会把这个洞忽略，结果看起来就像没有擦除。
            // 将内部擦除区域通过最短通道连接到边界后，再提取外轮廓即可保留擦除结果。
            OpenCvSharp.Cv2.FindContours(mask, out OpenCvSharp.Point[][] allContours, out _,
                OpenCvSharp.RetrievalModes.CComp, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            if (allContours.Length > 1)
            {
                var (endpoint, boundaryPoint) = FindNearestPolygonBoundary(polygon, rasterStroke);
                OpenCvSharp.Cv2.Line(mask, endpoint, boundaryPoint, OpenCvSharp.Scalar.Black,
                    Math.Max(1, thickness), OpenCvSharp.LineTypes.AntiAlias);
            }

            OpenCvSharp.Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _,
                OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            var replacements = contours
                .Where(contour => contour.Length >= 3 && OpenCvSharp.Cv2.ContourArea(contour) >= 6)
                .Select(contour => BuildPolygonFromContour(source.ClassName, contour, rasterWidth, rasterHeight))
                .ToList();
            _annotation.Objects.RemoveAt(index);
            _annotation.Objects.InsertRange(index, replacements);
            changed = true;
        }

        if (!changed) return;
        _eraserChanged = true;
        AnnotationList.ItemsSource = null;
        AnnotationList.ItemsSource = _annotation.Objects;
        RenderAnnotations();
        StatusText.Text = $"正在擦除；橡皮擦直径 {_eraserDiameter:0} 像素。松开左键后自动保存。";
    }

    private static (OpenCvSharp.Point Endpoint, OpenCvSharp.Point Boundary) FindNearestPolygonBoundary(
        OpenCvSharp.Point[] polygon, OpenCvSharp.Point[] stroke)
    {
        var firstEndpoint = stroke[0];
        var firstBoundary = ClosestPointOnPolygon(firstEndpoint, polygon, out var firstDistance);
        var lastEndpoint = stroke[^1];
        var lastBoundary = ClosestPointOnPolygon(lastEndpoint, polygon, out var lastDistance);
        return lastDistance < firstDistance
            ? (lastEndpoint, lastBoundary)
            : (firstEndpoint, firstBoundary);
    }

    private static OpenCvSharp.Point ClosestPointOnPolygon(
        OpenCvSharp.Point point, OpenCvSharp.Point[] polygon, out double distanceSquared)
    {
        var bestPoint = polygon[0];
        distanceSquared = double.MaxValue;
        for (var index = 0; index < polygon.Length; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Length];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * (double)dx + dy * (double)dy;
            var t = lengthSquared < double.Epsilon
                ? 0
                : ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            t = Math.Clamp(t, 0, 1);
            var candidateX = start.X + t * dx;
            var candidateY = start.Y + t * dy;
            var candidateDistance = Math.Pow(point.X - candidateX, 2) + Math.Pow(point.Y - candidateY, 2);
            if (candidateDistance >= distanceSquared) continue;
            distanceSquared = candidateDistance;
            bestPoint = new OpenCvSharp.Point((int)Math.Round(candidateX), (int)Math.Round(candidateY));
        }
        return bestPoint;
    }

    private void PaintStroke(IReadOnlyList<Point> stroke)
    {
        if (_annotation is null || stroke.Count == 0 || AnnotationCanvas.ActualWidth < 1 || AnnotationCanvas.ActualHeight < 1)
        {
            return;
        }

        var imageRect = GetImageRect();
        if (imageRect.Width < 1 || imageRect.Height < 1) return;
        var rasterWidth = Math.Clamp((int)Math.Round(imageRect.Width), 64, 2048);
        var rasterHeight = Math.Clamp((int)Math.Round(imageRect.Height), 64, 2048);
        var rasterStroke = stroke.Select(point => new OpenCvSharp.Point(
            Math.Clamp((int)Math.Round((point.X - imageRect.Left) / imageRect.Width * rasterWidth), 0, rasterWidth - 1),
            Math.Clamp((int)Math.Round((point.Y - imageRect.Top) / imageRect.Height * rasterHeight), 0, rasterHeight - 1))).ToArray();
        var thickness = Math.Max(2, (int)Math.Round(_brushDiameter / imageRect.Width * rasterWidth));

        using var mask = new OpenCvSharp.Mat(rasterHeight, rasterWidth, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.Black);
        if (rasterStroke.Length == 1)
        {
            OpenCvSharp.Cv2.Circle(mask, rasterStroke[0], Math.Max(1, thickness / 2), OpenCvSharp.Scalar.White, -1);
        }
        else
        {
            OpenCvSharp.Cv2.Polylines(mask, [rasterStroke], false, OpenCvSharp.Scalar.White,
                thickness, OpenCvSharp.LineTypes.AntiAlias);
            OpenCvSharp.Cv2.Circle(mask, rasterStroke[0], Math.Max(1, thickness / 2), OpenCvSharp.Scalar.White, -1);
            OpenCvSharp.Cv2.Circle(mask, rasterStroke[^1], Math.Max(1, thickness / 2), OpenCvSharp.Scalar.White, -1);
        }

        OpenCvSharp.Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _,
            OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
        var paintedObjects = contours
            .Where(contour => contour.Length >= 3 && OpenCvSharp.Cv2.ContourArea(contour) >= 6)
            .Select(contour => BuildPolygonFromContour(string.Empty, contour, rasterWidth, rasterHeight))
            .ToList();
        if (paintedObjects.Count == 0) return;
        if (!TryResolveAnnotationClass(out var className))
        {
            StatusText.Text = "已取消本次画刷绘制。";
            return;
        }
        foreach (var paintedObject in paintedObjects)
            paintedObject.ClassName = className;

        _annotation.Objects.AddRange(paintedObjects);
        _brushChanged = true;
        RefreshAnnotationList(_annotation.Objects.Count - 1);
        RenderAnnotations();
        StatusText.Text = $"画刷已添加 {paintedObjects.Count} 个分割区域；画刷直径 {_brushDiameter:0} 像素。";
    }

    private static DatasetAnnotationObject BuildPolygonFromContour(
        string className, OpenCvSharp.Point[] contour, int width, int height)
    {
        var simplified = OpenCvSharp.Cv2.ApproxPolyDP(contour, 1.0, true);
        var points = simplified.Select(point => new DatasetPoint
        {
            X = Math.Clamp(point.X / (double)Math.Max(1, width - 1), 0, 1),
            Y = Math.Clamp(point.Y / (double)Math.Max(1, height - 1), 0, 1),
        }).ToList();
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        return new DatasetAnnotationObject
        {
            ClassName = className,
            Shape = "polygon",
            X = minX,
            Y = minY,
            Width = maxX - minX,
            Height = maxY - minY,
            Polygon = points,
        };
    }

    private void SaveAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _image is null || _annotation is null) return;
        AppServices.Instance.Datasets.SaveAnnotation(_dataset, _annotation);
        ImageList.ItemsSource = AppServices.Instance.Datasets.ListImages(_dataset);
        UpdateImageListEmptyState();
        ImageList.SelectedItem = ((IEnumerable<DatasetImageItem>)ImageList.ItemsSource)
            .FirstOrDefault(x => x.RelativePath == _image.RelativePath);
        StatusText.Text = $"标注已保存：{_image.RelativePath}，共 {_annotation.Objects.Count} 个对象。";
    }

    private void AnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderAnnotations();
        if (_annotation is not null && AnnotationList.SelectedIndex >= 0 && AnnotationList.SelectedIndex < _annotation.Objects.Count)
        {
            StatusText.Text = "已选中标注。左键长按可拖动，选中框的边缘或角点可调整大小，右键可切换类别或删除。";
        }
    }

    private void AnnotationCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderAnnotations();

    private void AnnotationScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AnnotationScrollViewer.Zoom <= 1.001)
        {
            AnnotationScrollViewer.FitToWindow();
        }
    }

    private void AnnotationCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((!_eraserMode && !_brushMode) || AnnotationImage.Source is null) return;
        e.Handled = true;
        var delta = e.Delta > 0 ? 6 : -6;
        if (_brushMode)
        {
            _brushDiameter = Math.Clamp(_brushDiameter + delta, 8, 240);
            _brushCursorPoint = Mouse.GetPosition(AnnotationCanvas);
            UpdateBrushVisuals();
            StatusText.Text = $"画刷直径：{_brushDiameter:0} 像素；按住左键拖动画刷。";
        }
        else
        {
            _eraserDiameter = Math.Clamp(_eraserDiameter + delta, 8, 240);
            _eraserCursorPoint = Mouse.GetPosition(AnnotationCanvas);
            RenderAnnotations();
            StatusText.Text = $"橡皮擦直径：{_eraserDiameter:0} 像素；按住左键拖动擦除。";
        }
    }

    private void ResetZoom()
    {
        _zoom = 1;
        if (ZoomText is not null) ZoomText.Text = "缩放：100%";
        AnnotationScrollViewer?.FitToWindow();
    }

    private void RequestAutoFit()
    {
        if (AnnotationScrollViewer.Zoom <= 1.001)
        {
            AnnotationScrollViewer.FitToWindow();
        }
    }

    private void RenderAnnotations()
    {
        if (AnnotationCanvas is null) return;
        AnnotationCanvas.Children.Clear();
        _brushStrokeVisual = null;
        _brushCursorVisual = null;
        _eraserStrokeVisual = null;
        _eraserCursorVisual = null;
        if (_annotation is null) return;
        if (!_showAnnotationInfo) return;
        var imageRect = GetImageRect();
        var selected = AnnotationList.SelectedIndex;
        var normalStroke = OverlayStrokeThickness(1.1);
        var selectedStroke = OverlayStrokeThickness(1.6);
        for (var i = 0; i < _annotation.Objects.Count; i++)
        {
            var obj = _annotation.Objects[i];
            var hasPolygon = obj.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && obj.Polygon.Count >= 3;
            var semanticPolygon = _dataset?.TaskType == "semantic_segmentation" && hasPolygon;
            var contourOnly = _dataset?.TaskType == "detection" && GetYoloeOutputMode() == "contours";
            if (!semanticPolygon && !contourOnly && _showBoundingBoxes)
            {
                var rectangle = new Rectangle
                {
                    Width = Math.Max(1, obj.Width * imageRect.Width),
                    Height = Math.Max(1, obj.Height * imageRect.Height),
                    Stroke = i == selected ? Brushes.Lime : Brushes.Orange,
                    StrokeThickness = i == selected ? selectedStroke : normalStroke,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rectangle, imageRect.Left + obj.X * imageRect.Width);
                Canvas.SetTop(rectangle, imageRect.Top + obj.Y * imageRect.Height);
                AnnotationCanvas.Children.Add(rectangle);
            }
            var label = new TextBlock
            {
                Text = obj.ClassName,
                Foreground = Brushes.White,
                Background = i == selected
                    ? new SolidColorBrush(Color.FromArgb(190, 0, 105, 70))
                    : new SolidColorBrush(Color.FromArgb(180, 190, 100, 0)),
                FontSize = 11,
                Padding = new Thickness(1, 0, 1, 0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, imageRect.Left + obj.X * imageRect.Width);
            Canvas.SetTop(label, Math.Max(imageRect.Top, imageRect.Top + obj.Y * imageRect.Height - 18));
            AnnotationCanvas.Children.Add(label);
            if (hasPolygon && _showContours)
            {
                var polygon = new Polygon
                {
                    Points = new PointCollection(obj.Polygon.Select(point =>
                        new Point(imageRect.Left + point.X * imageRect.Width, imageRect.Top + point.Y * imageRect.Height))),
                    Stroke = i == selected ? Brushes.Lime : Brushes.Orange,
                    StrokeThickness = i == selected ? selectedStroke : normalStroke,
                    Fill = new SolidColorBrush(Color.FromArgb(18, 255, 165, 0)),
                    IsHitTestVisible = false,
                };
                AnnotationCanvas.Children.Add(polygon);
            }
        }
        if (_sam1HoverPreview is { Polygon.Count: >= 3 } preview)
        {
            AnnotationCanvas.Children.Add(new Polygon
            {
                Points = new PointCollection(preview.Polygon.Select(point =>
                    new Point(imageRect.Left + point.X * imageRect.Width, imageRect.Top + point.Y * imageRect.Height))),
                Stroke = Brushes.Cyan,
                StrokeThickness = OverlayStrokeThickness(1.2),
                StrokeDashArray = [5, 3],
                Fill = new SolidColorBrush(Color.FromArgb(28, 0, 220, 255)),
                IsHitTestVisible = false,
            });
        }
        if (_sam1ClickMode && _sam1Prompts.Count == 0 && _sam1HoverPoint is { } hoverPoint)
        {
            var hoverMarker = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Cyan,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(hoverMarker, hoverPoint.X - hoverMarker.Width / 2);
            Canvas.SetTop(hoverMarker, hoverPoint.Y - hoverMarker.Height / 2);
            AnnotationCanvas.Children.Add(hoverMarker);
        }
        foreach (var prompt in _sam1Prompts)
        {
            var marker = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = prompt.Label == 1 ? Brushes.LimeGreen : Brushes.Red,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, imageRect.Left + prompt.X * imageRect.Width - marker.Width / 2);
            Canvas.SetTop(marker, imageRect.Top + prompt.Y * imageRect.Height - marker.Height / 2);
            AnnotationCanvas.Children.Add(marker);
        }
        if (_eraserMode && _eraserCursorPoint is { } eraserPoint)
        {
            _eraserStrokeVisual = new Polyline
            {
                Points = new PointCollection(_eraserStrokePoints),
                Stroke = new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)),
                StrokeThickness = _eraserDiameter,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            AnnotationCanvas.Children.Add(_eraserStrokeVisual);
            _eraserCursorVisual = new Ellipse
            {
                Width = _eraserDiameter,
                Height = _eraserDiameter,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeDashArray = [3, 2],
                Fill = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_eraserCursorVisual, eraserPoint.X - _eraserCursorVisual.Width / 2);
            Canvas.SetTop(_eraserCursorVisual, eraserPoint.Y - _eraserCursorVisual.Height / 2);
            AnnotationCanvas.Children.Add(_eraserCursorVisual);
        }
        if (_brushMode && _brushCursorPoint is { } brushPoint)
        {
            _brushStrokeVisual = new Polyline
            {
                Points = new PointCollection(_brushStrokePoints),
                Stroke = new SolidColorBrush(Color.FromArgb(150, 40, 220, 120)),
                StrokeThickness = _brushDiameter,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            AnnotationCanvas.Children.Add(_brushStrokeVisual);
            _brushCursorVisual = new Ellipse
            {
                Width = _brushDiameter,
                Height = _brushDiameter,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 2,
                StrokeDashArray = [3, 2],
                Fill = new SolidColorBrush(Color.FromArgb(45, 40, 220, 120)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_brushCursorVisual, brushPoint.X - _brushCursorVisual.Width / 2);
            Canvas.SetTop(_brushCursorVisual, brushPoint.Y - _brushCursorVisual.Height / 2);
            AnnotationCanvas.Children.Add(_brushCursorVisual);
        }
    }

    private double OverlayStrokeThickness(double baseThickness)
    {
        // The annotation canvas is zoomed together with the image. Keep overlay
        // lines visually thin at high zoom instead of scaling them into a blur.
        var zoom = Math.Max(1, AnnotationScrollViewer?.Zoom ?? 1);
        return Math.Max(0.75, baseThickness / zoom);
    }

    private void UpdateEraserVisuals()
    {
        if (!_eraserMode || _eraserCursorPoint is not { } point) return;
        if (_eraserStrokeVisual is null || _eraserCursorVisual is null)
        {
            RenderAnnotations();
            return;
        }
        _eraserStrokeVisual.Points = new PointCollection(_eraserStrokePoints);
        _eraserStrokeVisual.StrokeThickness = _eraserDiameter;
        _eraserCursorVisual.Width = _eraserDiameter;
        _eraserCursorVisual.Height = _eraserDiameter;
        Canvas.SetLeft(_eraserCursorVisual, point.X - _eraserDiameter / 2);
        Canvas.SetTop(_eraserCursorVisual, point.Y - _eraserDiameter / 2);
    }

    private void UpdateBrushVisuals()
    {
        if (!_brushMode || _brushCursorPoint is not { } point) return;
        if (_brushStrokeVisual is null || _brushCursorVisual is null)
        {
            RenderAnnotations();
            return;
        }
        _brushStrokeVisual.Points = new PointCollection(_brushStrokePoints);
        _brushStrokeVisual.StrokeThickness = _brushDiameter;
        _brushCursorVisual.Width = _brushDiameter;
        _brushCursorVisual.Height = _brushDiameter;
        Canvas.SetLeft(_brushCursorVisual, point.X - _brushDiameter / 2);
        Canvas.SetTop(_brushCursorVisual, point.Y - _brushDiameter / 2);
    }

    private void ClearImageView()
    {
        ResetZoom();
        _image = null;
        _annotation = null;
        ResetSam1Interaction();
        _editingIndex = -1;
        _editMode = EditMode.None;
        _editOriginal = null;
        _dragging = false;
        _polygonPoints.Clear();
        _polygonDraft = null;
        AnnotationImage.Source = null;
        AnnotationList.ItemsSource = null;
        AnnotationCanvas.Children.Clear();
    }

    private void AutoSaveAnnotation(string reason)
    {
        if (_dataset is null || _image is null || _annotation is null) return;
        AppServices.Instance.Datasets.SaveAnnotation(_dataset, _annotation);
        var relativePath = _image.RelativePath;
        RefreshImageList(relativePath, suppressSelectionChanged: true);
        StatusText.Text = $"{reason}已自动保存。";
    }

    private Rect GetImageRect()
    {
        var canvasWidth = Math.Max(1, AnnotationCanvas.ActualWidth);
        var canvasHeight = Math.Max(1, AnnotationCanvas.ActualHeight);
        if (AnnotationImage.Source is not BitmapSource source || source.Width <= 0 || source.Height <= 0)
            return new Rect(0, 0, canvasWidth, canvasHeight);

        var scale = Math.Min(canvasWidth / source.Width, canvasHeight / source.Height);
        var imageWidth = Math.Max(1, source.Width * scale);
        var imageHeight = Math.Max(1, source.Height * scale);
        return new Rect(
            (canvasWidth - imageWidth) / 2,
            (canvasHeight - imageHeight) / 2,
            imageWidth,
            imageHeight);
    }

    private static bool TryGetImageNormalizedPoint(Point point, Rect imageRect, out Point normalized)
    {
        if (!imageRect.Contains(point))
        {
            normalized = new Point();
            return false;
        }

        normalized = new Point(
            Math.Clamp((point.X - imageRect.Left) / imageRect.Width, 0, 1),
            Math.Clamp((point.Y - imageRect.Top) / imageRect.Height, 0, 1));
        return true;
    }

    private static Point ClampPointToRect(Point point, Rect rect) => new(
        Math.Clamp(point.X, rect.Left, rect.Right),
        Math.Clamp(point.Y, rect.Top, rect.Bottom));

    private int HitTestAnnotation(Point point)
    {
        if (_annotation is null) return -1;
        var imageRect = GetImageRect();
        if (!TryGetImageNormalizedPoint(point, imageRect, out var normalized)) return -1;
        var x = normalized.X;
        var y = normalized.Y;
        var toleranceX = 8 / imageRect.Width;
        var toleranceY = 8 / imageRect.Height;
        for (var index = _annotation.Objects.Count - 1; index >= 0; index--)
        {
            var obj = _annotation.Objects[index];
            if (obj.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && obj.Polygon.Count >= 3)
            {
                if (IsPointInsidePolygon(x, y, obj.Polygon)) return index;
                continue;
            }
            if (x >= obj.X - toleranceX && x <= obj.X + obj.Width + toleranceX &&
                y >= obj.Y - toleranceY && y <= obj.Y + obj.Height + toleranceY)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsPointInsidePolygon(double x, double y, IReadOnlyList<DatasetPoint> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
            if ((current.Y > y) != (previous.Y > y) &&
                x < ((previous.X - current.X) * (y - current.Y) / (previous.Y - current.Y)) + current.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private EditMode GetEditMode(Point point, DatasetAnnotationObject obj)
    {
        var imageRect = GetImageRect();
        if (!TryGetImageNormalizedPoint(point, imageRect, out var normalized)) return EditMode.None;
        var x = normalized.X;
        var y = normalized.Y;
        var toleranceX = 10 / imageRect.Width;
        var toleranceY = 10 / imageRect.Height;
        var left = Math.Abs(x - obj.X) <= toleranceX;
        var right = Math.Abs(x - (obj.X + obj.Width)) <= toleranceX;
        var top = Math.Abs(y - obj.Y) <= toleranceY;
        var bottom = Math.Abs(y - (obj.Y + obj.Height)) <= toleranceY;
        if (left && top) return EditMode.TopLeft;
        if (right && top) return EditMode.TopRight;
        if (left && bottom) return EditMode.BottomLeft;
        if (right && bottom) return EditMode.BottomRight;
        if (left) return EditMode.Left;
        if (right) return EditMode.Right;
        if (top) return EditMode.Top;
        if (bottom) return EditMode.Bottom;
        return EditMode.Move;
    }

    private void UpdateEditedAnnotation(Point current)
    {
        if (_annotation is null || _editOriginal is null || _editingIndex < 0 || _editingIndex >= _annotation.Objects.Count) return;
        var imageRect = GetImageRect();
        var dx = (current.X - _editStart.X) / imageRect.Width;
        var dy = (current.Y - _editStart.Y) / imageRect.Height;
        var original = _editOriginal;
        var obj = _annotation.Objects[_editingIndex];
        var originalRight = original.X + original.Width;
        var originalBottom = original.Y + original.Height;

        switch (_editMode)
        {
            case EditMode.Move:
                obj.X = Math.Clamp(original.X + dx, 0, 1 - original.Width);
                obj.Y = Math.Clamp(original.Y + dy, 0, 1 - original.Height);
                break;
            case EditMode.Left:
            case EditMode.TopLeft:
            case EditMode.BottomLeft:
                var newLeft = Math.Clamp(original.X + dx, 0, originalRight - 0.005);
                obj.X = newLeft;
                obj.Width = Math.Max(0.005, originalRight - newLeft);
                break;
        }

        switch (_editMode)
        {
            case EditMode.Right:
            case EditMode.TopRight:
            case EditMode.BottomRight:
                obj.Width = Math.Max(0.005, Math.Clamp(originalRight + dx, original.X + 0.005, 1) - original.X);
                break;
        }

        switch (_editMode)
        {
            case EditMode.Top:
            case EditMode.TopLeft:
            case EditMode.TopRight:
                var newTop = Math.Clamp(original.Y + dy, 0, originalBottom - 0.005);
                obj.Y = newTop;
                obj.Height = Math.Max(0.005, originalBottom - newTop);
                break;
        }

        switch (_editMode)
        {
            case EditMode.Bottom:
            case EditMode.BottomLeft:
            case EditMode.BottomRight:
                obj.Height = Math.Max(0.005, Math.Clamp(originalBottom + dy, original.Y + 0.005, 1) - original.Y);
                break;
        }
    }

    private static DatasetAnnotationObject CloneAnnotationObject(DatasetAnnotationObject source) => new()
    {
        ClassName = source.ClassName,
        Shape = source.Shape,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height,
        Polygon = source.Polygon.Select(point => new DatasetPoint { X = point.X, Y = point.Y }).ToList(),
    };

    private bool IsPolygonMode() =>
        _dataset?.TaskType is "semantic_segmentation" or "instance_segmentation";

    private void ManualDraw_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPolygonMode()) return;
        ResetSam1Interaction();
        _manualDrawMode = true;
        _brushMode = false;
        _eraserMode = false;
        _brushDragging = false;
        _eraserDragging = false;
        _brushCursorPoint = null;
        _eraserCursorPoint = null;
        AnnotationCanvas.ReleaseMouseCapture();
        CancelPolygonDraft();
        UpdateTaskTypeUi();
        RenderAnnotations();
        StatusText.Text = "手动绘制已开启：依次单击添加轮廓点，双击完成并保存分割区域。";
    }

    private void Brush_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPolygonMode()) return;
        ResetSam1Interaction();
        _manualDrawMode = false;
        _brushMode = true;
        _eraserMode = false;
        _brushDragging = false;
        _eraserDragging = false;
        _brushCursorPoint = null;
        _eraserCursorPoint = null;
        AnnotationCanvas.ReleaseMouseCapture();
        CancelPolygonDraft();
        UpdateTaskTypeUi();
        RenderAnnotations();
        StatusText.Text = $"画刷已开启：按住左键绘制分割区域，滚动鼠标调节画刷大小；当前直径 {_brushDiameter:0} 像素。";
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPolygonMode()) return;
        ResetSam1Interaction();
        _manualDrawMode = false;
        _brushMode = false;
        _eraserMode = true;
        _eraserDragging = false;
        _brushDragging = false;
        _brushCursorPoint = null;
        _eraserCursorPoint = null;
        CancelPolygonDraft();
        UpdateTaskTypeUi();
        RenderAnnotations();
        StatusText.Text = $"橡皮擦已开启：按住左键拖动擦除，滚轮调节大小；当前直径 {_eraserDiameter:0} 像素。";
    }

    private void CancelPolygonDraft()
    {
        _polygonPoints.Clear();
        _polygonDraft = null;
    }

    private void UpdateToolButtons()
    {
        ManualDrawButton.Content = "外轮廓绘制";
        BrushButton.Content = "画刷";
        EraserButton.Content = "橡皮擦";
    }

    private void UpdateTaskTypeUi()
    {
        var taskType = _dataset?.TaskType;
        if (taskType is not ("detection" or "semantic_segmentation" or "instance_segmentation"))
        {
            taskType = "detection";
        }
        var isDetection = taskType == "detection";
        var isSegmentation = taskType is "semantic_segmentation" or "instance_segmentation";
        var typeName = taskType switch
        {
            "detection" => "目标检测",
            "semantic_segmentation" => "语义分割",
            "instance_segmentation" => "实例分割",
            _ => "未选择",
        };
        AnnotationScrollViewer.WheelZoomEnabled = !_eraserMode && !_brushMode;
        DatasetTaskTypeText.Text = $"标注类型：{typeName}";
        YoloeButton.Visibility = isDetection ? Visibility.Visible : Visibility.Collapsed;
        YoloeOutputModeLabel.Visibility = isDetection ? Visibility.Visible : Visibility.Collapsed;
        YoloeOutputModeCombo.Visibility = isDetection ? Visibility.Visible : Visibility.Collapsed;
        YoloeButton.IsEnabled = !_yoloeRunning;
        if (!_yoloeRunning)
        {
            YoloeButton.Content = "智能标注";
        }
        ManualDrawButton.Visibility = isSegmentation ? Visibility.Visible : Visibility.Collapsed;
        BrushButton.Visibility = isSegmentation ? Visibility.Visible : Visibility.Collapsed;
        EraserButton.Visibility = isSegmentation ? Visibility.Visible : Visibility.Collapsed;
        PromptClassAfterDrawCheckBox.Visibility = isSegmentation ? Visibility.Visible : Visibility.Collapsed;
        Sam1Button.Visibility = IsSegmentationPlatform
            ? Visibility.Visible
            : Visibility.Collapsed;
        FinishShortcutHint.Visibility = isSegmentation ? Visibility.Visible : Visibility.Collapsed;
        Sam1Button.Content = "智能标注";
        if (!isSegmentation)
        {
            _sam1ClickMode = false;
            _manualDrawMode = false;
            _brushMode = false;
            _eraserMode = false;
        }
        else if (!_sam1ClickMode && !_manualDrawMode && !_brushMode && !_eraserMode)
        {
            _manualDrawMode = true;
        }
        UpdateToolButtons();
    }

    private async void YoloeCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset?.TaskType != "detection" || _yoloeRunning) return;
        if (!TryBuildYoloeMemoryRequest(out var prompts, out var reference)) return;
        var target = _image!;
        var outputMode = GetYoloeOutputMode();
        _yoloeRunning = true;
        _yoloeSpinnerFrame = 0;
        YoloeButton.IsEnabled = false;
        YoloeButton.Content = "⟳ 处理中...";
        _yoloeSpinnerTimer.Start();
        try
        {
            StatusText.Text = "YOLOE 正在根据当前框提示识别当前图片，请稍候……";
            var results = await AppServices.Instance.SmartAnnotations.RunYoloEAsync(
                reference.FullPath, prompts, [target.FullPath], outputMode: outputMode);
            ApplySmartResults(results, "当前图片", outputMode);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"YOLOE 自动标注失败：{ex.Message}";
        }
        finally
        {
            _yoloeSpinnerTimer.Stop();
            _yoloeRunning = false;
            YoloeButton.IsEnabled = true;
            YoloeButton.Content = "智能标注";
        }
    }

    private async void YoloePropagate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildYoloeRequest(out var prompts, out var reference)) return;
        var images = ImageList.Items.OfType<DatasetImageItem>().ToList();
        var index = images.FindIndex(x => x.RelativePath == reference.RelativePath);
        var targets = index >= 0 ? images.Skip(index + 1).ToList() : [];
        if (targets.Count == 0)
        {
            StatusText.Text = "当前图片后面没有可传播的图片。";
            return;
        }
        try
        {
            StatusText.Text = $"YOLOE 正在传播到 {targets.Count} 张后续图片，请稍候……";
            var results = await AppServices.Instance.SmartAnnotations.RunYoloEAsync(
                reference.FullPath, prompts, targets.Select(x => x.FullPath).ToArray(),
                outputMode: GetYoloeOutputMode());
            ApplySmartResults(results, $"{targets.Count} 张后续图片", GetYoloeOutputMode());
        }
        catch (Exception ex)
        {
            StatusText.Text = $"YOLOE 跨图传播失败：{ex.Message}";
        }
    }

    private void Sam1ClickMode_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset?.TaskType is not ("semantic_segmentation" or "instance_segmentation"))
        {
            StatusText.Text = "请先在左侧新建或选择一个语义分割/实例分割数据集。";
            return;
        }
        if (_dataset is null || _image is null || _annotation is null || ClassCombo.SelectedItem is not string)
        {
            StatusText.Text = "请先选择数据集、图片和标注类别。";
            return;
        }
        if (_sam1ClickMode)
        {
            var selectedPath = _image?.RelativePath;
            ++_sam1PromptVersion;
            ClearSam1HoverPreview();
            _sam1ClickMode = false;
            _manualDrawMode = true;
            _brushMode = false;
            _eraserMode = false;
            _sam1Prompts.Clear();
            _sam1ResultIndex = -1;
            UpdateTaskTypeUi();
            if (selectedPath is not null) RefreshImageList(selectedPath);
            RenderAnnotations();
            StatusText.Text = "本轮智能标注已完成。";
            return;
        }
        _sam1ClickMode = true;
        _manualDrawMode = false;
        _brushMode = false;
        _eraserMode = false;
        CancelPolygonDraft();
        _sam1Prompts.Clear();
        _sam1ResultIndex = -1;
        ClearSam1HoverPreview();
        Sam1Button.Content = "完成智能标注";
        UpdateToolButtons();
        StatusText.Text = $"{AppServices.Instance.SmartAnnotations.DescribeSam1Availability()}；移动鼠标可预览，左键添加目标点，右键添加排除点。";
        if (AnnotationCanvas.IsMouseOver)
            ScheduleSam1Hover(Mouse.GetPosition(AnnotationCanvas));
    }

    private async Task AddSam1PromptAsync(Point canvasPoint, int label)
    {
        if (_image is null || _annotation is null || ClassCombo.SelectedItem is not string className) return;
        if (!TryGetImageNormalizedPoint(canvasPoint, GetImageRect(), out var normalizedPoint)) return;
        if (label == 0 && _sam1Prompts.All(point => point.Label == 0))
        {
            StatusText.Text = "请先用左键点击需要保留的目标，再用右键排除多余区域。";
            return;
        }
        ClearSam1HoverPreview();
        _sam1Prompts.Add(new Sam1Prompt(
            normalizedPoint.X,
            normalizedPoint.Y,
            label));
        var promptVersion = ++_sam1PromptVersion;
        var image = _image;
        RenderAnnotations();
        try
        {
            var positive = _sam1Prompts.Count(point => point.Label == 1);
            var negative = _sam1Prompts.Count - positive;
            StatusText.Text = $"智能标注正在计算：{positive} 个目标点，{negative} 个排除点……";
            var result = await AppServices.Instance.SmartAnnotations.RunSam1ClickAsync(
                image.FullPath, _sam1Prompts.ToArray(), className);
            if (promptVersion != _sam1PromptVersion || !ReferenceEquals(image, _image)) return;
            if (result is null)
            {
                StatusText.Text = "智能标注没有返回有效分割区域。";
                return;
            }
            var datasetObject = ToDatasetObject(result);
            if (_sam1ResultIndex >= 0 && _sam1ResultIndex < _annotation.Objects.Count)
                _annotation.Objects[_sam1ResultIndex] = datasetObject;
            else
            {
                _annotation.Objects.Add(datasetObject);
                _sam1ResultIndex = _annotation.Objects.Count - 1;
            }
            RefreshAnnotationList(_sam1ResultIndex);
            RenderAnnotations();
            AppServices.Instance.Datasets.SaveAnnotation(_dataset!, _annotation);
            StatusText.Text = $"智能标注掩膜已更新：左键继续保留，右键继续排除；目标点 {positive}，排除点 {negative}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"智能标注失败：{ex.Message}";
        }
    }

    private async void DatasetAnnotationPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !IsTextInputFocused() && (_manualDrawMode || _brushMode))
        {
            e.Handled = true;
            CancelCurrentDraw();
            return;
        }
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None && !IsTextInputFocused())
        {
            e.Handled = true;
            EndCurrentAnnotation();
            return;
        }
        if (e.Key == Key.Z &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            IsPolygonMode() && !_sam1ClickMode && !IsTextInputFocused())
        {
            e.Handled = true;
            UndoLastSegmentationAnnotation();
            return;
        }
        if (!_sam1ClickMode || e.Key != Key.Z ||
            (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        await UndoLastSam1PromptAsync();
    }

    private void CancelCurrentDraw()
    {
        _dragging = false;
        _draft = null;
        _polygonPoints.Clear();
        _polygonDraft = null;
        _brushDragging = false;
        _brushChanged = false;
        _brushStrokePoints.Clear();
        AnnotationCanvas.ReleaseMouseCapture();
        RenderAnnotations();
        StatusText.Text = "已按 Esc 取消本次绘制，已有标注保持不变。";
    }

    private void UndoLastSegmentationAnnotation()
    {
        if (_manualDrawMode && (_polygonPoints.Count > 0 || _dragging))
        {
            CancelCurrentDraw();
            StatusText.Text = "已撤销当前未完成的外轮廓绘制。";
            return;
        }
        if (_brushMode && (_brushStrokePoints.Count > 0 || _brushDragging))
        {
            CancelCurrentDraw();
            StatusText.Text = "已撤销当前未完成的画刷绘制。";
            return;
        }
        if (_annotation is null || _annotation.Objects.Count == 0)
        {
            StatusText.Text = "当前没有可以撤销的分割标注。";
            return;
        }

        _annotation.Objects.RemoveAt(_annotation.Objects.Count - 1);
        RefreshAnnotationList(Math.Min(_annotation.Objects.Count - 1, AnnotationList.SelectedIndex));
        RenderAnnotations();
        AutoSaveAnnotation("撤销分割标注");
    }

    private void EndCurrentAnnotation()
    {
        if (_sam1ClickMode)
        {
            ++_sam1PromptVersion;
            ClearSam1HoverPreview();
            _sam1ClickMode = true;
            _manualDrawMode = false;
            _brushMode = false;
            _eraserMode = false;
            _eraserDragging = false;
            _sam1Prompts.Clear();
            _sam1ResultIndex = -1;
            AnnotationCanvas.ReleaseMouseCapture();
            UpdateToolButtons();
            RenderAnnotations();
            StatusText.Text = "S：本轮智能标注已结束，可继续点击生成下一个目标。";
            if (AnnotationCanvas.IsMouseOver)
                ScheduleSam1Hover(Mouse.GetPosition(AnnotationCanvas));
            return;
        }

        if (_eraserMode)
        {
            if (_eraserDragging)
            {
                _eraserDragging = false;
                AnnotationCanvas.ReleaseMouseCapture();
                EraseStroke(_eraserStrokePoints);
                _eraserStrokePoints.Clear();
                if (_eraserChanged) AutoSaveAnnotation("橡皮擦轨迹");
            }
            _eraserMode = true;
            _eraserCursorPoint = null;
            _manualDrawMode = false;
            UpdateToolButtons();
            RenderAnnotations();
            StatusText.Text = "S：本次橡皮擦操作已结束，可继续擦除下一个区域。";
            return;
        }

        if (_brushMode)
        {
            if (_brushDragging)
            {
                _brushDragging = false;
                AnnotationCanvas.ReleaseMouseCapture();
                PaintStroke(_brushStrokePoints);
                _brushStrokePoints.Clear();
                if (_brushChanged) AutoSaveAnnotation("画刷轨迹");
            }
            _brushMode = true;
            _brushCursorPoint = null;
            _manualDrawMode = false;
            UpdateToolButtons();
            RenderAnnotations();
            StatusText.Text = "S：本次画刷操作已结束，可继续绘制下一个区域。";
            return;
        }

        if (_manualDrawMode)
        {
            if (_polygonPoints.Count >= 3)
            {
                FinishPolygon();
            }
            else if (_polygonPoints.Count > 0)
            {
                CancelPolygonDraft();
                RenderAnnotations();
            }
            _manualDrawMode = true;
            AnnotationCanvas.ReleaseMouseCapture();
            UpdateToolButtons();
            RenderAnnotations();
            StatusText.Text = "S：本次手动绘制已结束，可继续绘制下一个。";
        }
    }

    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox;

    private async Task UndoLastSam1PromptAsync()
    {
        if (_image is null || _annotation is null || _dataset is null ||
            ClassCombo.SelectedItem is not string className)
        {
            return;
        }
        if (_sam1Prompts.Count == 0)
        {
            StatusText.Text = "当前没有可以撤销的智能标注点击。";
            return;
        }

        var removed = _sam1Prompts[^1];
        _sam1Prompts.RemoveAt(_sam1Prompts.Count - 1);
        var version = ++_sam1PromptVersion;
        var image = _image;
        ClearSam1HoverPreview();

        if (_sam1Prompts.Count == 0)
        {
            if (_sam1ResultIndex >= 0 && _sam1ResultIndex < _annotation.Objects.Count)
            {
                _annotation.Objects.RemoveAt(_sam1ResultIndex);
                _sam1ResultIndex = -1;
                AppServices.Instance.Datasets.SaveAnnotation(_dataset, _annotation);
                RefreshImageList(image.RelativePath, suppressSelectionChanged: true);
            }
            RefreshAnnotationList(-1);
            RenderAnnotations();
            StatusText.Text = $"已撤销最近的智能标注 {(removed.Label == 1 ? "目标点" : "排除点")}；本轮掩膜已移除。";
            if (AnnotationCanvas.IsMouseOver)
                ScheduleSam1Hover(Mouse.GetPosition(AnnotationCanvas));
            return;
        }

        RenderAnnotations();
        try
        {
            StatusText.Text = "正在撤销最近的智能标注点击并重新计算掩膜……";
            var prompts = _sam1Prompts.ToArray();
            var result = await AppServices.Instance.SmartAnnotations.RunSam1ClickAsync(
                image.FullPath, prompts, className);
            if (version != _sam1PromptVersion || !ReferenceEquals(image, _image) || result is null) return;

            var datasetObject = ToDatasetObject(result);
            if (_sam1ResultIndex >= 0 && _sam1ResultIndex < _annotation.Objects.Count)
                _annotation.Objects[_sam1ResultIndex] = datasetObject;
            else
            {
                _annotation.Objects.Add(datasetObject);
                _sam1ResultIndex = _annotation.Objects.Count - 1;
            }
            RefreshAnnotationList(_sam1ResultIndex);
            RenderAnnotations();
            AppServices.Instance.Datasets.SaveAnnotation(_dataset, _annotation);
            RefreshImageList(image.RelativePath, suppressSelectionChanged: true);
            StatusText.Text = $"已撤销最近的智能标注 {(removed.Label == 1 ? "目标点" : "排除点")}，掩膜已恢复到上一步。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"智能标注撤销失败：{ex.Message}";
        }
    }

    private void ResetSam1Interaction()
    {
        ResetSam1ImageState();
        _sam1ClickMode = false;
    }

    private void ResetSam1ImageState()
    {
        ClearSam1HoverPreview();
        _sam1Prompts.Clear();
        _sam1ResultIndex = -1;
        _sam1PromptVersion++;
    }

    private async void Sam1HoverTimer_Tick(object? sender, EventArgs e)
    {
        _sam1HoverTimer.Stop();
        if (_sam1HoverBusy || !_sam1ClickMode || _sam1Prompts.Count > 0 ||
            _sam1HoverPoint is not { } canvasPoint || _image is null ||
            ClassCombo.SelectedItem is not string className)
        {
            return;
        }

        var version = _sam1HoverVersion;
        var image = _image;
        if (!TryGetImageNormalizedPoint(canvasPoint, GetImageRect(), out var normalizedPoint)) return;
        var prompt = new Sam1Prompt(
            normalizedPoint.X,
            normalizedPoint.Y,
            1);
        _sam1HoverBusy = true;
        try
        {
            StatusText.Text = "智能标注正在生成悬浮预览……";
            var result = await AppServices.Instance.SmartAnnotations.RunSam1ClickAsync(
                image.FullPath, [prompt], className);
            if (_sam1ClickMode && _sam1Prompts.Count == 0 && _sam1HoverPoint is not null &&
                ReferenceEquals(image, _image))
            {
                _sam1HoverPreview = result;
                RenderAnnotations();
                StatusText.Text = "青色区域为预览；左键确认目标，右键可在确认后排除多余区域。";
            }
        }
        catch (Exception ex)
        {
            if (_sam1ClickMode) StatusText.Text = $"智能标注悬浮预览失败：{ex.Message}";
        }
        finally
        {
            _sam1HoverBusy = false;
            if (_sam1ClickMode && _sam1Prompts.Count == 0 && version != _sam1HoverVersion && _sam1HoverPoint is not null)
            {
                _sam1HoverTimer.Start();
            }
        }
    }

    private void ScheduleSam1Hover(Point canvasPoint)
    {
        if (_sam1HoverPoint is { } previous &&
            Math.Abs(previous.X - canvasPoint.X) < 1 &&
            Math.Abs(previous.Y - canvasPoint.Y) < 1)
        {
            return;
        }

        _sam1HoverPoint = canvasPoint;
        _sam1HoverVersion++;
        // Restarting the timer for every MouseMove acts as a debounce, so inference
        // never starts until movement stops. Keep the current request running and
        // submit the newest retained point as soon as the worker becomes available.
        if (!_sam1HoverBusy && !_sam1HoverTimer.IsEnabled)
        {
            _sam1HoverTimer.Start();
        }
        RenderAnnotations();
        StatusText.Text = "已捕捉悬浮位置，正在生成智能标注预览……";
    }

    private void ClearSam1HoverPreview()
    {
        _sam1HoverTimer.Stop();
        _sam1HoverPoint = null;
        _sam1HoverPreview = null;
        _sam1HoverVersion++;
    }

    private bool TryBuildYoloeMemoryRequest(out IReadOnlyList<YoloEPrompt> prompts, out DatasetImageItem reference)
    {
        prompts = [];
        reference = null!;
        if (_dataset is null || _image is null || _annotation is null)
        {
            StatusText.Text = "请先选择数据集和图片。";
            return false;
        }

        var images = ImageList.Items.OfType<DatasetImageItem>().ToList();
        var currentIndex = images.FindIndex(image => PathsEqual(image.FullPath, _image.FullPath));
        var candidates = new List<(DatasetImageItem Image, DatasetAnnotation Annotation)>();
        if (BuildYoloePrompts(_annotation).Count > 0)
            candidates.Add((_image, _annotation));

        // 当前图没有标注时，按图像顺序从前往后找最近一张已保存的标注图。
        if (candidates.Count == 0 && currentIndex > 0)
        {
            for (var index = currentIndex - 1; index >= 0; index--)
            {
                var annotation = AppServices.Instance.Datasets.LoadAnnotation(_dataset, images[index].RelativePath);
                if (annotation.Objects.Count > 0)
                {
                    candidates.Add((images[index], annotation));
                    break;
                }
            }
        }

        foreach (var candidate in candidates)
        {
            var candidatePrompts = BuildYoloePrompts(candidate.Annotation);
            if (candidatePrompts.Count > 0)
            {
                reference = candidate.Image;
                prompts = candidatePrompts;
                return true;
            }
        }

        StatusText.Text = "当前图片没有可用的 YOLOE 标注记忆。请先手动画框并选择类别，标注会在操作完成后自动保存。";
        ThemedMessageBox.Show(
            "还没有可用的标注记忆。请先在当前图片上画至少一个矩形框并选择类别，标注完成后会自动保存，再使用 YOLOE 自动标注。",
            "需要先建立标注记忆",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private IReadOnlyList<YoloEPrompt> BuildYoloePrompts(DatasetAnnotation annotation)
    {
        if (_dataset is null) return [];
        var classIds = _dataset.Classes
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        return annotation.Objects
            .Where(x => x.Width > 0 && x.Height > 0 && classIds.ContainsKey(x.ClassName))
            .Select(x => new YoloEPrompt(x.ClassName, classIds[x.ClassName], x.X, x.Y, x.Width, x.Height))
            .ToArray();
    }

    private bool TryBuildYoloeRequest(out IReadOnlyList<YoloEPrompt> prompts, out DatasetImageItem target)
    {
        prompts = [];
        target = null!;
        if (_dataset is null || _image is null || _annotation is null)
        {
            StatusText.Text = "请先选择数据集和参考图片。";
            return false;
        }
        var classIds = _dataset.Classes
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var items = _annotation.Objects
            .Where(x => x.Width > 0 && x.Height > 0 && classIds.ContainsKey(x.ClassName))
            .Select(x => new YoloEPrompt(x.ClassName, classIds[x.ClassName], x.X, x.Y, x.Width, x.Height))
            .ToArray();
        if (items.Length == 0)
        {
            StatusText.Text = "YOLOE 需要当前图片至少一个有效矩形框作为提示；多边形会使用其外接框。";
            return false;
        }
        prompts = items;
        target = _image;
        return true;
    }

    private string GetYoloeOutputMode() =>
        (YoloeOutputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "both";

    private void ApplySmartResults(IReadOnlyList<SmartAnnotationImageResult> results, string scope, string outputMode = "both")
    {
        if (_dataset is null) return;
        var images = ImageList.Items.OfType<DatasetImageItem>().ToList();
        var written = 0;
        foreach (var result in results)
        {
            var image = images.FirstOrDefault(x => PathsEqual(x.FullPath, result.ImagePath));
            if (image is null) continue;
            var annotation = AppServices.Instance.Datasets.LoadAnnotation(_dataset, image.RelativePath);
            annotation.Objects = result.Objects
                .Where(x => x.Width > 0 && x.Height > 0)
                .Where(x => outputMode != "contours" || x.Polygon.Count >= 3)
                .Select(x => NormalizeYoloeObject(x, outputMode))
                .Select(ToDatasetObject)
                .ToList();
            AppServices.Instance.Datasets.SaveAnnotation(_dataset, annotation);
            written++;
            if (PathsEqual(image.FullPath, _image?.FullPath ?? ""))
            {
                _annotation = annotation;
                RefreshAnnotationList(-1);
                RenderAnnotations();
            }
        }
        ImageList.ItemsSource = AppServices.Instance.Datasets.ListImages(_dataset);
        UpdateImageListEmptyState();
        StatusText.Text = $"YOLOE 已完成 {scope}：写入 {written} 张图片的自动标注；后续修改也会自动保存。";
    }

    private static SmartAnnotationObjectResult NormalizeYoloeObject(SmartAnnotationObjectResult source, string outputMode)
    {
        if (outputMode == "boxes")
        {
            return new SmartAnnotationObjectResult
            {
                ClassName = source.ClassName,
                Shape = "bbox",
                X = source.X,
                Y = source.Y,
                Width = source.Width,
                Height = source.Height,
                Score = source.Score,
            };
        }

        if (outputMode == "contours" && source.Polygon.Count >= 3)
        {
            return new SmartAnnotationObjectResult
            {
                ClassName = source.ClassName,
                Shape = "polygon",
                X = source.X,
                Y = source.Y,
                Width = source.Width,
                Height = source.Height,
                Polygon = source.Polygon.ToList(),
                Score = source.Score,
            };
        }

        return source;
    }

    private static DatasetAnnotationObject ToDatasetObject(SmartAnnotationObjectResult source) => new()
    {
        ClassName = source.ClassName,
        Shape = source.Shape,
        X = Math.Clamp(source.X, 0, 1),
        Y = Math.Clamp(source.Y, 0, 1),
        Width = Math.Clamp(source.Width, 0, 1),
        Height = Math.Clamp(source.Height, 0, 1),
        Polygon = source.Polygon.Select(point => new DatasetPoint
        {
            X = Math.Clamp(point.X, 0, 1),
            Y = Math.Clamp(point.Y, 0, 1),
        }).ToList(),
    };

    private void RefreshAnnotationList(int selectedIndex)
    {
        AnnotationList.ItemsSource = null;
        AnnotationList.ItemsSource = _annotation?.Objects;
        AnnotationList.SelectedIndex = selectedIndex;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static List<string> ParseClasses(string text) => text
        .Split([',', ';', '，', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void UpdateRectangle(Rectangle rectangle, Point start, Point end)
    {
        Canvas.SetLeft(rectangle, Math.Min(start.X, end.X));
        Canvas.SetTop(rectangle, Math.Min(start.Y, end.Y));
        rectangle.Width = Math.Abs(end.X - start.X);
        rectangle.Height = Math.Abs(end.Y - start.Y);
    }
}
