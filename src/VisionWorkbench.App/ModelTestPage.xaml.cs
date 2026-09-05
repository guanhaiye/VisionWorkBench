using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionWorkbench.Application;
using IoPath = System.IO.Path;

namespace VisionWorkbench.App;

public partial class ModelTestPage : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private YoloModelTestResult? _result;
    private readonly Dictionary<string, YoloModelTestResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private string _loadedModelText = "";
    private readonly bool _semanticMode;
    private readonly string? _taskTypeLabel;

    public ModelTestPage(bool semanticMode = false, string? taskTypeLabel = null)
    {
        _semanticMode = semanticMode;
        _taskTypeLabel = taskTypeLabel;
        InitializeComponent();
        SemanticOptionsPanel.Visibility = Visibility.Collapsed;
        DetectionOptionsPanel.Visibility = semanticMode ? Visibility.Collapsed : Visibility.Visible;
        if (semanticMode)
        {
            TaskTypeText.Visibility = Visibility.Collapsed;
        }
        else if (!string.IsNullOrWhiteSpace(taskTypeLabel))
        {
            TaskTypeText.Text = $"模型类型：{taskTypeLabel}";
        }
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "YOLO 模型 (*.engine;*.pt)|*.engine;*.pt|TensorRT Engine (*.engine)|*.engine|PyTorch 模型 (*.pt)|*.pt",
            CheckFileExists = true
        };
        var initialDirectory = GetModelInitialDirectory();
        if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        if (dialog.ShowDialog() == true)
        {
            ModelPathText.Text = dialog.FileName;
            _results.Clear();
            TaskTypeText.Text = "模型类型：将在测试时自动识别";
        }
    }

    private string? GetModelInitialDirectory()
    {
        if (File.Exists(ModelPathText.Text))
            return IoPath.GetDirectoryName(IoPath.GetFullPath(ModelPathText.Text));

        var datasets = AppServices.Instance.Datasets.List();
        foreach (var dataset in datasets)
        {
            if (string.IsNullOrWhiteSpace(dataset.RootDirectory)) continue;
            var modelDirectory = IoPath.Combine(dataset.RootDirectory, ".visionworkbench", "models");
            if (Directory.Exists(modelDirectory)) return modelDirectory;
        }

        return datasets
            .Select(dataset => dataset.RootDirectory)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.webp", Multiselect = true };
        if (dialog.ShowDialog() == true) SetImages(dialog.FileNames);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        SetImages(Directory.EnumerateFiles(dialog.FolderName)
            .Where(path => ImageExtensions.Contains(IoPath.GetExtension(path))));
    }

    private void SetImages(IEnumerable<string> paths)
    {
        var images = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new TestImage(path)).ToList();
        _results.Clear();
        ImageList.ItemsSource = images;
        if (images.Count > 0) ImageList.SelectedIndex = 0;
        StatusText.Text = $"已加载 {images.Count} 张测试图片。";
    }

    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _result = null;
        ResultList.ItemsSource = null;
        if (ImageList.SelectedItem is not TestImage image) return;
        PreviewImage.Source = LoadBitmap(image.FullPath);
        PreviewViewer.FitToWindow();
        OverlayCanvas.Children.Clear();
        if (_results.TryGetValue(image.FullPath, out var result))
        {
            ShowResult(result);
            StatusText.Text = $"已显示测试结果：{image.FileName}";
        }
        else StatusText.Text = $"当前图片尚未测试：{image.FileName}";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var images = ImageList.Items.OfType<TestImage>().ToList();
        if (!File.Exists(ModelPathText.Text) || images.Count == 0)
        {
            ThemedMessageBox.Show("请先选择有效模型和测试图片。", "模型测试");
            return;
        }
        if (!double.TryParse(ConfidenceText.Text, out var confidence) || confidence is < 0 or > 1 ||
            !double.TryParse(IouText.Text, out var iou) || iou is < 0 or > 1)
        {
            ThemedMessageBox.Show("置信度和 IoU 必须在 0～1 之间。", "参数错误");
            return;
        }
        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        if (_semanticMode)
        {
            await RunSemanticAsync(images, device);
            return;
        }
        var inferenceModel = YoloModelTestService.ResolveInferenceModelPath(ModelPathText.Text);
        var selectedPt = IoPath.GetExtension(ModelPathText.Text).Equals(".pt", StringComparison.OrdinalIgnoreCase);
        if (selectedPt && string.Equals(inferenceModel, IoPath.GetFullPath(ModelPathText.Text), StringComparison.OrdinalIgnoreCase))
        {
            var choice = ThemedMessageBox.Show(
                "当前 PT 模型没有同名 TensorRT Engine。\n\n是否现在转换为 Engine 快速模型？\n\n" +
                "Engine 使用 TensorRT 加速，可以显著提高推理速度，推理效果与 PT 模型基本一致。\n" +
                "选择“是”：转换完成后只加载 Engine。\n选择“否”：直接加载 PT。",
                "转换为 TensorRT Engine", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)
            {
                var convertingText = new TextBlock { Text = "正在将 PT 模型转换为 TensorRT Engine……\n首次转换可能需要几分钟。",
                    Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap };
                var convertingBar = new ProgressBar { IsIndeterminate = true, Height = 22, Margin = new Thickness(18, 0, 18, 18) };
                var convertingPanel = new StackPanel(); convertingPanel.Children.Add(convertingText); convertingPanel.Children.Add(convertingBar);
                var convertingFrame = new Border
                {
                    Background = (Brush)FindResource("SurfaceBrush"),
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = convertingPanel,
                };
                var convertingWindow = new Window { Title = "正在转换 TensorRT Engine", Width = 470, Height = 150,
                    Style = (Style)FindResource("ThemedDialogWindow"),
                    Owner = Window.GetWindow(this), Content = convertingFrame };
                try
                {
                    RunButton.IsEnabled = false;
                    convertingWindow.Show();
                    inferenceModel = await AppServices.Instance.YoloModelTest.ExportTensorRtAsync(ModelPathText.Text);
                    StatusText.Text = $"Engine 转换完成：{inferenceModel}";
                }
                catch (Exception ex)
                {
                    ThemedMessageBox.Show($"TensorRT Engine 转换失败：\n{ex.Message}", "转换失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = $"Engine 转换失败：{ex.Message}";
                    return;
                }
                finally
                {
                    convertingWindow.Close();
                    RunButton.IsEnabled = true;
                }
            }
        }
        var usingEngine = IoPath.GetExtension(inferenceModel).Equals(".engine", StringComparison.OrdinalIgnoreCase);
        var inferenceDevice = usingEngine ? "0" : device;
        _loadedModelText = usingEngine
            ? $"实际加载：TensorRT Engine（{IoPath.GetFileName(inferenceModel)}）"
            : $"实际加载：PyTorch（{IoPath.GetFileName(inferenceModel)}）";
        var progressBar = new ProgressBar { Minimum = 0, Maximum = images.Count, Height = 22, Margin = new Thickness(18, 8, 18, 18) };
        var progressText = new TextBlock { Margin = new Thickness(18, 18, 18, 4), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel(); panel.Children.Add(progressText); panel.Children.Add(progressBar);
        var progressFrame = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel,
        };
        var progressWindow = new Window { Title = "批量模型测试", Width = 460, Height = 150,
            Style = (Style)FindResource("ThemedDialogWindow"),
            Owner = Window.GetWindow(this), Content = progressFrame };
        RunButton.IsEnabled = false;
        _results.Clear();
        var failures = new List<string>();
        try
        {
            progressWindow.Show();
            TaskTypeText.Text = _loadedModelText;
            var completed = 0;
            var batchProgress = new Progress<YoloBatchTestItem>(item =>
            {
                completed++;
                if (item.Result is not null) _results[item.ImagePath] = item.Result;
                else failures.Add($"{IoPath.GetFileName(item.ImagePath)}：{item.Error}");
                progressText.Text = $"正在推理 {completed}/{images.Count}\n{IoPath.GetFileName(item.ImagePath)}";
                StatusText.Text = $"批量测试进度：{completed}/{images.Count}";
                progressBar.Value = completed;
            });
            await AppServices.Instance.YoloModelTest.RunBatchAsync(inferenceModel,
                images.Select(image => image.FullPath).ToList(), confidence, iou, inferenceDevice, batchProgress);
            if (ImageList.SelectedItem is TestImage selected && _results.TryGetValue(selected.FullPath, out var selectedResult))
            {
                ShowResult(selectedResult);
            }
            StatusText.Text = failures.Count == 0
                ? $"批量测试完成：共 {images.Count} 张。切换图片可直接查看结果。"
                : $"批量测试完成：成功 {_results.Count} 张，失败 {failures.Count} 张。";
            if (failures.Count > 0) ThemedMessageBox.Show(string.Join("\n", failures.Take(10)), "部分图片测试失败");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"测试失败：{ex.Message}";
            var logDirectory = IoPath.Combine(AppServices.Instance.Settings.DataDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = IoPath.Combine(logDirectory, "model-test.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n", System.Text.Encoding.UTF8);
            ThemedMessageBox.Show($"测试失败：\n{ex.Message}\n\n详细日志：{logPath}", "模型测试",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWindow.Close();
            RunButton.IsEnabled = true;
        }
    }

    private async Task RunSemanticAsync(IReadOnlyList<TestImage> images, string device)
    {
        var progressBar = new ProgressBar { Minimum = 0, Maximum = images.Count, Height = 22, Margin = new Thickness(18, 8, 18, 18) };
        var progressText = new TextBlock { Margin = new Thickness(18, 18, 18, 4), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel(); panel.Children.Add(progressText); panel.Children.Add(progressBar);
        var frame = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel,
        };
        var window = new Window { Title = "ATU5 语义分割测试", Width = 460, Height = 150,
            Style = (Style)FindResource("ThemedDialogWindow"), Owner = Window.GetWindow(this), Content = frame };
        RunButton.IsEnabled = false;
        _results.Clear();
        var failures = new List<string>();
        try
        {
            window.Show();
            var completed = 0;
            var progress = new Progress<YoloBatchTestItem>(item =>
            {
                completed++;
                if (item.Result is not null) _results[item.ImagePath] = item.Result;
                else failures.Add($"{IoPath.GetFileName(item.ImagePath)}：{item.Error}");
                progressText.Text = $"正在推理 {completed}/{images.Count}\n{IoPath.GetFileName(item.ImagePath)}";
                progressBar.Value = completed;
                StatusText.Text = $"ATU5 测试进度：{completed}/{images.Count}";
            });
            await AppServices.Instance.Atu5ModelTest.RunBatchAsync(ModelPathText.Text,
                images.Select(image => image.FullPath).ToList(), device, progress);
            if (ImageList.SelectedItem is TestImage selected && _results.TryGetValue(selected.FullPath, out var result)) ShowResult(result);
            StatusText.Text = failures.Count == 0 ? $"ATU5 语义分割测试完成：共 {images.Count} 张。" : $"测试完成：成功 {_results.Count} 张，失败 {failures.Count} 张。";
            if (failures.Count > 0) ThemedMessageBox.Show(string.Join("\n", failures.Take(10)), "部分图片测试失败");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"ATU5 测试失败：{ex.Message}";
            ThemedMessageBox.Show(ex.Message, "ATU5 语义分割测试", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            window.Close();
            RunButton.IsEnabled = true;
        }
    }

    private void ShowResult(YoloModelTestResult result)
    {
        _result = result;
        var isSemantic = string.Equals(result.Task, "semantic", StringComparison.OrdinalIgnoreCase);
        DetectionOptionsPanel.Visibility = isSemantic ? Visibility.Collapsed : Visibility.Visible;
        var taskText = result.Task == "segment" ? "模型类型：实例分割（segment）" : "模型类型：目标检测（detect）";
        TaskTypeText.Text = string.IsNullOrWhiteSpace(_loadedModelText) ? taskText : $"{taskText}\n{_loadedModelText}";
        if (result.Task == "semantic") TaskTypeText.Text = $"模型类型：ATU5 / FPN 语义分割\n{_loadedModelText}";
        SummaryText.Text = $"目标 {result.Detections.Count} 个；掩膜 {result.Masks.Count} 个；推理 {result.ElapsedMs:0.0} ms";
        ResultList.ItemsSource = result.Detections.Select(item => $"{item.ClassName}  {item.Confidence:P1}").ToList();
        if (isSemantic)
        {
            SummaryText.Text = $"语义区域 {result.Masks.Count} 个；推理 {result.ElapsedMs:0.0} ms";
            ResultList.ItemsSource = result.Masks.Select(item => $"{item.ClassName}  {item.Confidence:P1}").ToList();
        }
        RenderResult();
    }

    private void OverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderResult();

    private void RenderResult()
    {
        OverlayCanvas.Children.Clear();
        if (_result is null || PreviewImage.Source is null) return;
        var sourceWidth = PreviewImage.Source.Width;
        var sourceHeight = PreviewImage.Source.Height;
        var scale = Math.Min(OverlayCanvas.ActualWidth / sourceWidth, OverlayCanvas.ActualHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var offsetX = (OverlayCanvas.ActualWidth - width) / 2;
        var offsetY = (OverlayCanvas.ActualHeight - height) / 2;
        foreach (var mask in _result.Masks)
        {
            OverlayCanvas.Children.Add(new Polygon
            {
                Points = new PointCollection(mask.Polygon.Select(point => new Point(offsetX + point.X * width, offsetY + point.Y * height))),
                Fill = new SolidColorBrush(Color.FromArgb(75, 0, 210, 255)), Stroke = Brushes.Cyan, StrokeThickness = 2,
                IsHitTestVisible = false,
            });
        }
        if (string.Equals(_result.Task, "semantic", StringComparison.OrdinalIgnoreCase)) return;

        foreach (var item in _result.Detections)
        {
            var rectangle = new Rectangle { Width = item.Width * width, Height = item.Height * height,
                Stroke = Brushes.Lime, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(rectangle, offsetX + item.X * width); Canvas.SetTop(rectangle, offsetY + item.Y * height);
            OverlayCanvas.Children.Add(rectangle);
            var label = new TextBlock { Text = $"{item.ClassName} {item.Confidence:P0}", Foreground = Brushes.White,
                Background = Brushes.DarkGreen, Padding = new Thickness(3, 1, 3, 1), IsHitTestVisible = false };
            Canvas.SetLeft(label, offsetX + item.X * width); Canvas.SetTop(label, Math.Max(0, offsetY + item.Y * height - 22));
            OverlayCanvas.Children.Add(label);
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
    }

    private sealed record TestImage(string FullPath) { public string FileName => IoPath.GetFileName(FullPath); }
}
