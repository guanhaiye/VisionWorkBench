using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionWorkbench.Application;
using IoPath = System.IO.Path;

namespace VisionWorkbench.App;

public partial class PoseAnnotationPage : UserControl
{
    private static readonly string[] DefaultKeypoints =
    ["鼻子", "左眼", "右眼", "左耳", "右耳", "左肩", "右肩", "左肘", "右肘", "左腕", "右腕", "左髋", "右髋", "左膝", "右膝", "左脚踝", "右脚踝"];
    private DatasetDefinition? _dataset;
    private DatasetImageItem? _image;
    private DatasetAnnotation? _annotation;
    private Rectangle? _draft;
    private Point _dragStart;
    private bool _dragging;
    private bool _keypointMode;
    private int _poseObjectIndex = -1;
    private readonly List<DatasetPoint> _pendingKeypoints = [];

    public PoseAnnotationPage()
    {
        InitializeComponent();
        ClearDatasetSelection();
        Loaded += (_, _) => RefreshDatasets();
        IsVisibleChanged += (_, args) => { if (args.NewValue is true) RefreshDatasets(_dataset?.Id); };
    }

    private void RefreshDatasets(string? selectId = null)
    {
        var datasets = AppServices.Instance.Datasets.List()
            .Where(item => string.Equals(item.TaskType, "pose", StringComparison.OrdinalIgnoreCase)).ToList();
        DatasetList.ItemsSource = datasets;
        if (selectId is not null) DatasetList.SelectedItem = datasets.FirstOrDefault(item => item.Id == selectId);
        if (DatasetList.SelectedItem is null && datasets.Count > 0) DatasetList.SelectedIndex = 0;
        if (DatasetList.SelectedItem is null) ClearDatasetSelection();
    }

    private void NewDataset_Click(object sender, RoutedEventArgs e)
    {
        _dataset = new DatasetDefinition { Name = $"pose-{DateTime.Now:MMddHHmmss}", TaskType = "pose", Classes = ["person"], KeypointNames = [.. DefaultKeypoints] };
        SetDatasetEditorEnabled(true);
        DatasetNameText.Text = _dataset.Name;
        DatasetRootText.Text = "";
        DatasetClassesText.Text = "person";
        KeypointNamesText.Text = string.Join(", ", _dataset.KeypointNames);
        ClassCombo.ItemsSource = _dataset.Classes;
        ClassCombo.SelectedIndex = 0;
        DatasetList.ItemsSource = new List<DatasetDefinition> { _dataset };
        DatasetList.SelectedItem = _dataset;
        ClearImageView();
        StatusText.Text = "已新建关键点数据集，请选择图片目录。";
    }

    private void LoadDataset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "加载关键点数据集目录" };
        if (dialog.ShowDialog() != true) return;
        var existing = AppServices.Instance.Datasets.List().FirstOrDefault(item =>
            string.Equals(IoPath.GetFullPath(item.RootDirectory), IoPath.GetFullPath(dialog.FolderName), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.TaskType != "pose") { ThemedMessageBox.Show("该目录不是关键点检测数据集。", "数据集类型"); return; }
            RefreshDatasets(existing.Id);
            return;
        }
        _dataset = new DatasetDefinition { Name = new DirectoryInfo(dialog.FolderName).Name, RootDirectory = dialog.FolderName, TaskType = "pose", Classes = ["person"], KeypointNames = [.. DefaultKeypoints] };
        _dataset = AppServices.Instance.Datasets.Save(_dataset);
        RefreshDatasets(_dataset.Id);
        StatusText.Text = $"已加载数据集目录，共 {AppServices.Instance.Datasets.ListImages(_dataset).Count} 张图片。";
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择关键点图片目录" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _dataset ??= new DatasetDefinition { Name = $"pose-{DateTime.Now:MMddHHmmss}", TaskType = "pose", Classes = ["person"], KeypointNames = ParseNames(KeypointNamesText.Text) };
            var target = CreateDatasetDirectory(dialog.FolderName, DatasetNameText.Text);
            CopyImages(dialog.FolderName, target);
            _dataset.RootDirectory = target;
            _dataset.Name = string.IsNullOrWhiteSpace(DatasetNameText.Text) ? new DirectoryInfo(dialog.FolderName).Name : DatasetNameText.Text.Trim();
            _dataset.Classes = ParseNames(DatasetClassesText.Text);
            _dataset.KeypointNames = ParseNames(KeypointNamesText.Text);
            _dataset = AppServices.Instance.Datasets.Save(_dataset);
            DatasetRootText.Text = target;
            RefreshDatasets(_dataset.Id);
            RefreshImages();
            StatusText.Text = $"图片已复制到独立数据集目录，共 {ImageList.Items.Count} 张。";
        }
        catch (Exception ex) { ThemedMessageBox.Show($"创建数据集失败：{ex.Message}", "关键点数据集", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SaveDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) { ThemedMessageBox.Show("请先新建或加载数据集。", "关键点数据集"); return; }
        try
        {
            _dataset.Name = DatasetNameText.Text.Trim();
            _dataset.Classes = ParseNames(DatasetClassesText.Text);
            _dataset.KeypointNames = ParseNames(KeypointNamesText.Text);
            if (_dataset.Classes.Count == 0 || _dataset.KeypointNames.Count == 0) throw new InvalidOperationException("至少配置一个类别和一个关键点。");
            _dataset = AppServices.Instance.Datasets.Save(_dataset);
            RefreshDatasets(_dataset.Id);
            StatusText.Text = $"数据集已保存：{_dataset.Name}，关键点数量 {_dataset.KeypointNames.Count}。";
        }
        catch (Exception ex) { ThemedMessageBox.Show($"保存失败：{ex.Message}", "关键点数据集", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void DatasetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatasetList.SelectedItem is not DatasetDefinition dataset)
        {
            ClearDatasetSelection();
            return;
        }
        SetDatasetEditorEnabled(true);
        _dataset = dataset;
        DatasetNameText.Text = dataset.Name;
        DatasetRootText.Text = dataset.RootDirectory;
        DatasetClassesText.Text = string.Join(", ", dataset.Classes);
        KeypointNamesText.Text = string.Join(", ", dataset.KeypointNames);
        ClassCombo.ItemsSource = dataset.Classes;
        ClassCombo.SelectedIndex = 0;
        RefreshImages();
    }

    private void ClearDatasetSelection()
    {
        _dataset = null;
        DatasetNameText.Text = "";
        DatasetRootText.Text = "";
        DatasetClassesText.Text = "";
        KeypointNamesText.Text = "";
        ClassCombo.ItemsSource = null;
        ClassCombo.SelectedIndex = -1;
        ClearImageView();
        SetDatasetEditorEnabled(false);
        StatusText.Text = "请先新建或加载关键点数据集。";
    }

    private void SetDatasetEditorEnabled(bool enabled)
    {
        DatasetNameText.IsEnabled = enabled;
        DatasetRootText.IsEnabled = enabled;
        DatasetClassesText.IsEnabled = enabled;
        KeypointNamesText.IsEnabled = enabled;
        ClassCombo.IsEnabled = enabled;
    }

    private void RefreshImages(string? selected = null)
    {
        if (_dataset is null) return;
        var images = AppServices.Instance.Datasets.ListImages(_dataset);
        ImageList.ItemsSource = images;
        if (selected is not null) ImageList.SelectedItem = images.FirstOrDefault(item => item.RelativePath == selected);
        if (ImageList.SelectedItem is null && images.Count > 0) ImageList.SelectedIndex = 0;
    }

    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dataset is null || ImageList.SelectedItem is not DatasetImageItem image) return;
        _image = image;
        _annotation = AppServices.Instance.Datasets.LoadAnnotation(_dataset, image.RelativePath);
        AnnotationImage.Source = LoadBitmap(image.FullPath);
        AnnotationViewer.FitToWindow();
        CancelPose();
        RefreshAnnotationList();
        RenderAnnotations();
        StatusText.Text = $"{image.FileName}：已有 {_annotation.Objects.Count} 个目标。";
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_annotation is null || _dataset is null || ClassCombo.SelectedItem is not string) return;
        var point = e.GetPosition(AnnotationCanvas);
        if (_keypointMode)
        {
            if (!TryNormalize(point, out var normalized)) return;
            _pendingKeypoints.Add(new DatasetPoint { X = normalized.X, Y = normalized.Y, Visibility = 2 });
            if (_pendingKeypoints.Count == _dataset.KeypointNames.Count)
            {
                _annotation.Objects[_poseObjectIndex].Keypoints = [.. _pendingKeypoints];
                _keypointMode = false;
                _pendingKeypoints.Clear();
                _poseObjectIndex = -1;
                RefreshAnnotationList();
                SaveCurrentAnnotation("关键点标注完成");
            }
            else
            {
                PoseHintText.Text = $"请点击第 {_pendingKeypoints.Count + 1} 个关键点：{_dataset.KeypointNames[_pendingKeypoints.Count]}";
                RenderAnnotations();
            }
            e.Handled = true;
            return;
        }
        if (!TryNormalize(point, out _)) return;
        _dragStart = point;
        _draft = new Rectangle { Stroke = Brushes.Yellow, StrokeThickness = 2, StrokeDashArray = [4, 2] };
        AnnotationCanvas.Children.Add(_draft);
        _dragging = true;
        AnnotationCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _draft is null) return;
        var end = ClampToImage(e.GetPosition(AnnotationCanvas));
        _draft.Width = Math.Abs(end.X - _dragStart.X);
        _draft.Height = Math.Abs(end.Y - _dragStart.Y);
        Canvas.SetLeft(_draft, Math.Min(_dragStart.X, end.X));
        Canvas.SetTop(_draft, Math.Min(_dragStart.Y, end.Y));
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _draft is null || _annotation is null || _dataset is null || ClassCombo.SelectedItem is not string className) return;
        var end = ClampToImage(e.GetPosition(AnnotationCanvas));
        var left = Math.Min(_dragStart.X, end.X); var top = Math.Min(_dragStart.Y, end.Y);
        var width = Math.Abs(end.X - _dragStart.X); var height = Math.Abs(end.Y - _dragStart.Y);
        _dragging = false; _draft = null; AnnotationCanvas.ReleaseMouseCapture();
        if (width < 5 || height < 5) { RenderAnnotations(); return; }
        var rect = GetImageRect();
        _annotation.Objects.Add(new DatasetAnnotationObject
        {
            ClassName = className, Shape = "pose",
            X = Math.Clamp((left - rect.Left) / rect.Width, 0, 1), Y = Math.Clamp((top - rect.Top) / rect.Height, 0, 1),
            Width = Math.Clamp(width / rect.Width, 0, 1), Height = Math.Clamp(height / rect.Height, 0, 1),
        });
        _poseObjectIndex = _annotation.Objects.Count - 1;
        _keypointMode = true;
        _pendingKeypoints.Clear();
        PoseHintText.Text = $"请点击第 1 个关键点：{_dataset.KeypointNames[0]}";
        RefreshAnnotationList(); RenderAnnotations();
        e.Handled = true;
    }

    private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_annotation is null) return;
        var point = e.GetPosition(AnnotationCanvas);
        var rect = GetImageRect();
        var index = _annotation.Objects.FindIndex(item => point.X >= rect.Left + item.X * rect.Width && point.X <= rect.Left + (item.X + item.Width) * rect.Width && point.Y >= rect.Top + item.Y * rect.Height && point.Y <= rect.Top + (item.Y + item.Height) * rect.Height);
        if (index >= 0)
        {
            var result = ThemedMessageBox.Show("确定删除这个关键点目标吗？", "删除标注", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) { _annotation.Objects.RemoveAt(index); CancelPose(); RefreshAnnotationList(); SaveCurrentAnnotation("删除关键点目标"); }
        }
        e.Handled = true;
    }

    private void CancelPose_Click(object sender, RoutedEventArgs e) => CancelPose();
    private void CancelPose() { _keypointMode = false; _pendingKeypoints.Clear(); _poseObjectIndex = -1; PoseHintText.Text = "先拖动绘制目标框，再依次点击关键点"; RenderAnnotations(); }

    private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation is null || _annotation.Objects.Count == 0) return;
        if (ThemedMessageBox.Show("确定清空当前图片的全部关键点标注吗？", "清空标注", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _annotation.Objects.Clear(); CancelPose(); RefreshAnnotationList(); SaveCurrentAnnotation("清空关键点标注");
    }

    private void RefreshAnnotationList() { AnnotationList.ItemsSource = null; AnnotationList.ItemsSource = _annotation?.Objects; }
    private void SaveCurrentAnnotation(string reason)
    {
        if (_dataset is null || _image is null || _annotation is null) return;
        AppServices.Instance.Datasets.SaveAnnotation(_dataset, _annotation); RefreshImages(_image.RelativePath); StatusText.Text = $"{reason}，已自动保存。";
    }
    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderAnnotations();

    private void ClearImageView()
    {
        _image = null;
        _annotation = null;
        CancelPose();
        AnnotationImage.Source = null;
        AnnotationList.ItemsSource = null;
        AnnotationCanvas.Children.Clear();
    }

    private void RenderAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        if (_annotation is null || AnnotationImage.Source is null) return;
        var rect = GetImageRect();
        for (var i = 0; i < _annotation.Objects.Count; i++)
        {
            var item = _annotation.Objects[i];
            var box = new Rectangle { Width = item.Width * rect.Width, Height = item.Height * rect.Height, Stroke = i == _poseObjectIndex ? Brushes.Lime : Brushes.Orange, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(box, rect.Left + item.X * rect.Width); Canvas.SetTop(box, rect.Top + item.Y * rect.Height); AnnotationCanvas.Children.Add(box);
            var label = new TextBlock { Text = item.ClassName, Foreground = Brushes.White, Background = Brushes.DarkOrange, Padding = new Thickness(2, 0, 2, 0), IsHitTestVisible = false };
            Canvas.SetLeft(label, rect.Left + item.X * rect.Width); Canvas.SetTop(label, Math.Max(rect.Top, rect.Top + item.Y * rect.Height - 18)); AnnotationCanvas.Children.Add(label);
            for (var pointIndex = 0; pointIndex < item.Keypoints.Count; pointIndex++)
                AddKeypointVisual(item.Keypoints[pointIndex], pointIndex, rect, Brushes.LimeGreen);
        }
        if (_keypointMode && _poseObjectIndex >= 0 && _poseObjectIndex < _annotation.Objects.Count)
            for (var index = 0; index < _pendingKeypoints.Count; index++) AddKeypointVisual(_pendingKeypoints[index], index, rect, Brushes.Cyan);
    }

    private void AddKeypointVisual(DatasetPoint point, int index, Rect rect, Brush brush)
    {
        var x = rect.Left + point.X * rect.Width; var y = rect.Top + point.Y * rect.Height;
        var marker = new Ellipse { Width = 12, Height = 12, Fill = brush, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(marker, x - 6); Canvas.SetTop(marker, y - 6); AnnotationCanvas.Children.Add(marker);
        var number = new TextBlock { Text = $"{index + 1}", Foreground = Brushes.White, Background = Brushes.Black, FontSize = 10, IsHitTestVisible = false };
        Canvas.SetLeft(number, x + 6); Canvas.SetTop(number, y - 8); AnnotationCanvas.Children.Add(number);
    }

    private bool TryNormalize(Point point, out Point normalized)
    {
        var rect = GetImageRect();
        if (!rect.Contains(point)) { normalized = new Point(); return false; }
        normalized = new Point(Math.Clamp((point.X - rect.Left) / rect.Width, 0, 1), Math.Clamp((point.Y - rect.Top) / rect.Height, 0, 1)); return true;
    }
    private Point ClampToImage(Point point) { var rect = GetImageRect(); return new Point(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom)); }
    private Rect GetImageRect()
    {
        var width = Math.Max(1, AnnotationCanvas.ActualWidth); var height = Math.Max(1, AnnotationCanvas.ActualHeight);
        if (AnnotationImage.Source is not BitmapSource source || source.Width <= 0 || source.Height <= 0) return new Rect(0, 0, width, height);
        var scale = Math.Min(width / source.Width, height / source.Height); var imageWidth = source.Width * scale; var imageHeight = source.Height * scale;
        return new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
    }
    private static BitmapImage LoadBitmap(string path) { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image; }
    private static List<string> ParseNames(string value) => value.Split([',', '，', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static string CreateDatasetDirectory(string source, string name)
    {
        var root = AppServices.Instance.Settings.DatasetDirectory; if (string.IsNullOrWhiteSpace(root)) root = IoPath.GetDirectoryName(source)!; Directory.CreateDirectory(root);
        var safe = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(source).Name : name.Trim(); foreach (var ch in IoPath.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
        var candidate = IoPath.Combine(root, safe); if (!Directory.Exists(candidate)) return candidate;
        return IoPath.Combine(root, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
    }
    private static void CopyImages(string source, string target)
    {
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories).Where(path => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(IoPath.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("目录中没有找到图片。");
        foreach (var file in files) { var relative = IoPath.GetRelativePath(source, file); var destination = IoPath.Combine(target, relative); Directory.CreateDirectory(IoPath.GetDirectoryName(destination)!); File.Copy(file, destination, false); }
    }
    private void AutoSplit_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) return;
        try { _dataset = AppServices.Instance.Datasets.AutoSplit(_dataset); RefreshImages(_image?.RelativePath); StatusText.Text = "已自动划分训练集和验证集。"; }
        catch (Exception ex) { ThemedMessageBox.Show($"划分失败：{ex.Message}", "数据集划分", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) return;
        var dialog = new OpenFolderDialog { Title = "选择 YOLO11 Pose 导出目录" }; if (dialog.ShowDialog() != true) return;
        try { var output = AppServices.Instance.Datasets.ExportYolo(_dataset, dialog.FolderName); StatusText.Text = $"YOLO11 Pose 数据集已导出：{output}"; }
        catch (Exception ex) { ThemedMessageBox.Show($"导出失败：{ex.Message}", "关键点数据集", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
