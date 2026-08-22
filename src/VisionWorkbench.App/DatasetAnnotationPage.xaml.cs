using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionWorkbench.Application;

namespace VisionWorkbench.App;

/// <summary>离线图片数据集管理与矩形框标注页面。</summary>
public partial class DatasetAnnotationPage : UserControl
{
    private DatasetDefinition? _dataset;
    private DatasetImageItem? _image;
    private DatasetAnnotation? _annotation;
    private Point _dragStart;
    private Rectangle? _draft;
    private bool _dragging;
    private bool _sam3ClickMode;
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

    public DatasetAnnotationPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDatasets();
    }

    private void RefreshDatasets(string? selectId = null)
    {
        var datasets = AppServices.Instance.Datasets.List();
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
        _dataset = new DatasetDefinition
        {
            Name = $"dataset-{DateTime.Now:MMddHHmmss}",
            Classes = ["object"],
        };
        DatasetNameText.Text = _dataset.Name;
        DatasetRootText.Text = "";
        DatasetClassesText.Text = "object";
        ClassCombo.ItemsSource = _dataset.Classes;
        ClassCombo.SelectedIndex = 0;
        ImageList.ItemsSource = null;
        ClearImageView();
        var datasets = AppServices.Instance.Datasets.List().ToList();
        datasets.Add(_dataset);
        DatasetList.ItemsSource = datasets;
        DatasetList.SelectedItem = _dataset;
        StatusText.Text = "已新建数据集单元。请填写图片目录和类别，然后保存数据集。";
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        var dialog = new OpenFolderDialog { Title = "选择图片数据集目录" };
        if (Directory.Exists(DatasetRootText.Text)) dialog.InitialDirectory = DatasetRootText.Text;
        if (dialog.ShowDialog() == true)
        {
            if (_dataset is null) NewDataset_Click(sender, e);
            DatasetRootText.Text = dialog.FolderName;
            _dataset!.RootDirectory = dialog.FolderName;
            var images = AppServices.Instance.Datasets.ListImages(_dataset);
            ImageList.ItemsSource = images;
            ImageList.SelectedIndex = images.Count > 0 ? 0 : -1;
            if (images.Count > 0)
            {
                StatusText.Text = $"已扫描图片目录，共发现 {images.Count} 张图片。请点击“保存数据集”完成登记。";
            }
            else
            {
                StatusText.Text = "目录已选择，但没有找到 jpg、jpeg、png 或 bmp 图片。请检查目录层级和文件格式。";
            }
        }
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
            MessageBox.Show($"保存数据集失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) { MessageBox.Show("请先选择或保存数据集。", "数据集"); return; }
        var dialog = new OpenFolderDialog { Title = "选择 YOLO 数据集导出目录" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var output = AppServices.Instance.Datasets.ExportYolo(_dataset, dialog.FolderName);
            StatusText.Text = $"YOLO 数据集已导出：{output}（包含 data.yaml、images 和 labels）";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DatasetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatasetList.SelectedItem is not DatasetDefinition dataset) return;
        _dataset = dataset;
        DatasetNameText.Text = dataset.Name;
        DatasetRootText.Text = dataset.RootDirectory;
        DatasetClassesText.Text = string.Join(", ", dataset.Classes);
        ClassCombo.ItemsSource = dataset.Classes;
        if (ClassCombo.Items.Count > 0) ClassCombo.SelectedIndex = 0;
        ImageList.ItemsSource = AppServices.Instance.Datasets.ListImages(dataset);
        ClearImageView();
        StatusText.Text = $"{dataset.Name}：{ImageList.Items.Count} 张图片。标注格式为矩形框，可导出 YOLO。";
    }

    private async void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dataset is null || ImageList.SelectedItem is not DatasetImageItem image) return;
        _image = image;
        _annotation = AppServices.Instance.Datasets.LoadAnnotation(_dataset, image.RelativePath);
        AnnotationList.ItemsSource = _annotation.Objects;
        AnnotationList.SelectedIndex = -1;
        try
        {
            AnnotationImage.Source = await Task.Run(() => LoadBitmap(image.FullPath));
            RenderAnnotations();
            StatusText.Text = $"当前图片：{image.RelativePath}，{_annotation.Objects.Count} 个标注。按住鼠标左键拖出矩形框。";
        }
        catch (Exception ex)
        {
            ClearImageView();
            StatusText.Text = $"图片加载失败：{ex.Message}";
        }
    }

    private void AnnotationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dataset is null || _image is null || _annotation is null || ClassCombo.SelectedItem is not string)
        {
            StatusText.Text = "请选择数据集、图片和标注类别。";
            return;
        }
        if (_sam3ClickMode)
        {
            _sam3ClickMode = false;
            e.Handled = true;
            _ = RunSam3ClickAsync(e.GetPosition(AnnotationCanvas));
            return;
        }
        var point = e.GetPosition(AnnotationCanvas);
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
        if (IsPolygonMode())
        {
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
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(AnnotationCanvas);
        _draft = new Rectangle { Stroke = Brushes.Yellow, StrokeThickness = 2, StrokeDashArray = [4, 2] };
        AnnotationCanvas.Children.Add(_draft);
        AnnotationCanvas.CaptureMouse();
    }

    private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
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
        UpdateRectangle(_draft, _dragStart, e.GetPosition(AnnotationCanvas));
    }

    private void AnnotationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
        var end = e.GetPosition(AnnotationCanvas);
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
        var canvasWidth = Math.Max(1, AnnotationCanvas.ActualWidth);
        var canvasHeight = Math.Max(1, AnnotationCanvas.ActualHeight);
        _annotation.Objects.Add(new DatasetAnnotationObject
        {
            ClassName = className,
            Shape = "bbox",
            X = Math.Clamp(left / canvasWidth, 0, 1),
            Y = Math.Clamp(top / canvasHeight, 0, 1),
            Width = Math.Clamp(width / canvasWidth, 0, 1),
            Height = Math.Clamp(height / canvasHeight, 0, 1),
        });
        AnnotationList.ItemsSource = null;
        AnnotationList.ItemsSource = _annotation.Objects;
        AnnotationList.SelectedIndex = _annotation.Objects.Count - 1;
        RenderAnnotations();
        AutoSaveAnnotation("新增标注");
    }

    private void AnnotationCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var index = HitTestAnnotation(e.GetPosition(AnnotationCanvas));
        if (index < 0) return;
        AnnotationList.SelectedIndex = index;
        ShowAnnotationContextMenu(index, AnnotationCanvas);
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

    private void ShowAnnotationContextMenu(int index, FrameworkElement placementTarget)
    {
        if (_dataset is null || _annotation is null || index < 0 || index >= _annotation.Objects.Count) return;
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        foreach (var className in _dataset.Classes)
        {
            var item = new MenuItem { Header = $"切换类别：{className}", Tag = className };
            item.Click += ChangeAnnotationClass_Click;
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "删除标注", Tag = index };
        delete.Click += DeleteAnnotationMenu_Click;
        menu.Items.Add(delete);
        menu.IsOpen = true;
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

    private void FinishPolygon()
    {
        if (_annotation is null || ClassCombo.SelectedItem is not string className || _polygonPoints.Count < 3)
        {
            return;
        }
        var canvasWidth = Math.Max(1, AnnotationCanvas.ActualWidth);
        var canvasHeight = Math.Max(1, AnnotationCanvas.ActualHeight);
        var points = _polygonPoints.Select(point => new DatasetPoint
        {
            X = Math.Clamp(point.X / canvasWidth, 0, 1),
            Y = Math.Clamp(point.Y / canvasHeight, 0, 1),
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
                StrokeThickness = 2,
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

    private void SaveAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _image is null || _annotation is null) return;
        AppServices.Instance.Datasets.SaveAnnotation(_dataset, _annotation);
        ImageList.ItemsSource = AppServices.Instance.Datasets.ListImages(_dataset);
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

    private void RenderAnnotations()
    {
        if (AnnotationCanvas is null) return;
        AnnotationCanvas.Children.Clear();
        if (_annotation is null) return;
        var selected = AnnotationList.SelectedIndex;
        for (var i = 0; i < _annotation.Objects.Count; i++)
        {
            var obj = _annotation.Objects[i];
            var rectangle = new Rectangle
            {
                Width = Math.Max(1, obj.Width * AnnotationCanvas.ActualWidth),
                Height = Math.Max(1, obj.Height * AnnotationCanvas.ActualHeight),
                Stroke = i == selected ? Brushes.Lime : Brushes.Orange,
                StrokeThickness = i == selected ? 3 : 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rectangle, obj.X * AnnotationCanvas.ActualWidth);
            Canvas.SetTop(rectangle, obj.Y * AnnotationCanvas.ActualHeight);
            AnnotationCanvas.Children.Add(rectangle);
            var label = new TextBlock
            {
                Text = obj.ClassName,
                Foreground = Brushes.White,
                Background = i == selected ? Brushes.DarkGreen : Brushes.DarkOrange,
                Padding = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, obj.X * AnnotationCanvas.ActualWidth);
            Canvas.SetTop(label, Math.Max(0, obj.Y * AnnotationCanvas.ActualHeight - 18));
            AnnotationCanvas.Children.Add(label);
            if (obj.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && obj.Polygon.Count >= 3)
            {
                var polygon = new Polygon
                {
                    Points = new PointCollection(obj.Polygon.Select(point =>
                        new Point(point.X * AnnotationCanvas.ActualWidth, point.Y * AnnotationCanvas.ActualHeight))),
                    Stroke = i == selected ? Brushes.Lime : Brushes.Orange,
                    StrokeThickness = i == selected ? 3 : 2,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 255, 165, 0)),
                    IsHitTestVisible = false,
                };
                AnnotationCanvas.Children.Add(polygon);
            }
        }
    }

    private void ClearImageView()
    {
        _image = null;
        _annotation = null;
        _sam3ClickMode = false;
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
        var images = AppServices.Instance.Datasets.ListImages(_dataset);
        ImageList.ItemsSource = images;
        ImageList.SelectedItem = images.FirstOrDefault(x => x.RelativePath == relativePath);
        StatusText.Text = $"{reason}已自动保存。";
    }

    private int HitTestAnnotation(Point point)
    {
        if (_annotation is null) return -1;
        var canvasWidth = Math.Max(1, AnnotationCanvas.ActualWidth);
        var canvasHeight = Math.Max(1, AnnotationCanvas.ActualHeight);
        var x = point.X / canvasWidth;
        var y = point.Y / canvasHeight;
        var toleranceX = 8 / canvasWidth;
        var toleranceY = 8 / canvasHeight;
        for (var index = _annotation.Objects.Count - 1; index >= 0; index--)
        {
            var obj = _annotation.Objects[index];
            if (x >= obj.X - toleranceX && x <= obj.X + obj.Width + toleranceX &&
                y >= obj.Y - toleranceY && y <= obj.Y + obj.Height + toleranceY)
            {
                return index;
            }
        }
        return -1;
    }

    private EditMode GetEditMode(Point point, DatasetAnnotationObject obj)
    {
        var canvasWidth = Math.Max(1, AnnotationCanvas.ActualWidth);
        var canvasHeight = Math.Max(1, AnnotationCanvas.ActualHeight);
        var x = point.X / canvasWidth;
        var y = point.Y / canvasHeight;
        var toleranceX = 10 / canvasWidth;
        var toleranceY = 10 / canvasHeight;
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
        var canvasWidth = Math.Max(1, AnnotationCanvas.ActualWidth);
        var canvasHeight = Math.Max(1, AnnotationCanvas.ActualHeight);
        var dx = (current.X - _editStart.X) / canvasWidth;
        var dy = (current.Y - _editStart.Y) / canvasHeight;
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
        ((AnnotationModeCombo.SelectedItem as ComboBoxItem)?.Tag as string) == "polygon";

    private async void YoloeCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildYoloeMemoryRequest(out var prompts, out var reference)) return;
        var target = _image!;
        try
        {
            StatusText.Text = "YOLOE 正在根据当前框提示识别当前图片，请稍候……";
            var results = await AppServices.Instance.SmartAnnotations.RunYoloEAsync(
                reference.FullPath, prompts, [target.FullPath]);
            ApplySmartResults(results, "当前图片");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"YOLOE 自动标注失败：{ex.Message}";
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
                reference.FullPath, prompts, targets.Select(x => x.FullPath).ToArray());
            ApplySmartResults(results, $"{targets.Count} 张后续图片");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"YOLOE 跨图传播失败：{ex.Message}";
        }
    }

    private void Sam3ClickMode_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || _image is null || _annotation is null || ClassCombo.SelectedItem is not string)
        {
            StatusText.Text = "请先选择数据集、图片和标注类别。";
            return;
        }
        _sam3ClickMode = true;
        StatusText.Text = $"{AppServices.Instance.SmartAnnotations.DescribeSam3Availability()}；请在图片上点击目标。";
    }

    private async Task RunSam3ClickAsync(Point canvasPoint)
    {
        if (_image is null || _annotation is null || ClassCombo.SelectedItem is not string className) return;
        var point = new DatasetPoint
        {
            X = Math.Clamp(canvasPoint.X / Math.Max(1, AnnotationCanvas.ActualWidth), 0, 1),
            Y = Math.Clamp(canvasPoint.Y / Math.Max(1, AnnotationCanvas.ActualHeight), 0, 1),
        };
        try
        {
            StatusText.Text = "SAM3 正在根据点击生成分割掩码，请稍候……";
            var result = await AppServices.Instance.SmartAnnotations.RunSam3ClickAsync(
                _image.FullPath, point, className);
            if (result is null)
            {
                StatusText.Text = "SAM3 没有返回有效分割区域。";
                return;
            }
            _annotation.Objects.Add(ToDatasetObject(result));
            RefreshAnnotationList(_annotation.Objects.Count - 1);
            AppServices.Instance.Datasets.SaveAnnotation(_dataset!, _annotation);
            ImageList.ItemsSource = AppServices.Instance.Datasets.ListImages(_dataset!);
            StatusText.Text = $"SAM3 点击分割已生成并保存：{_image.RelativePath}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"SAM3 点击分割失败：{ex.Message}";
        }
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
        MessageBox.Show(
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

    private void ApplySmartResults(IReadOnlyList<SmartAnnotationImageResult> results, string scope)
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
        StatusText.Text = $"YOLOE 已完成 {scope}：写入 {written} 张图片的自动标注；后续修改也会自动保存。";
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
