using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VisionWorkbench.App;

public enum AiCharacterPlatform
{
    Annotation,
    Training,
    Test,
}

public enum AiRecognitionKind
{
    Character,
    Barcode,
    QrCode,
}

/// <summary>AI 字符识别平台：字符标注、PaddleOCR-VL 训练配置与模型测试。</summary>
public partial class AiCharacterRecognitionPage : UserControl
{
    private const string ModelName = "PaddleOCR-VL-1.6";
    private readonly AiCharacterPlatform _platform;
    private readonly AiRecognitionKind _kind;
    private readonly Dictionary<string, string> _annotations = new(StringComparer.OrdinalIgnoreCase);
    private string? _annotationRoot;
    private string? _selectedAnnotationImage;
    private string? _selectedTestImage;

    public AiCharacterRecognitionPage(AiCharacterPlatform platform)
        : this(platform, AiRecognitionKind.Character)
    {
    }

    public AiCharacterRecognitionPage(AiCharacterPlatform platform, AiRecognitionKind kind)
    {
        _platform = platform;
        _kind = kind;
        InitializeComponent();
        SetPlatformUi();
        RuntimeStatusText.Text = DescribeRuntime();
    }

    private void SetPlatformUi()
    {
        var annotationVisible = _platform == AiCharacterPlatform.Annotation ? Visibility.Visible : Visibility.Collapsed;
        AnnotationPanel.Visibility = annotationVisible;
        AnnotationToolbarPanel.Visibility = annotationVisible;
        TrainingPanel.Visibility = _platform == AiCharacterPlatform.Training ? Visibility.Visible : Visibility.Collapsed;
        TestPanel.Visibility = _platform == AiCharacterPlatform.Test ? Visibility.Visible : Visibility.Collapsed;
        var label = KindLabel;
        PlatformTitleText.Text = _platform switch
        {
            AiCharacterPlatform.Training => $"AI{label}识别训练平台",
            AiCharacterPlatform.Test => $"AI{label}识别测试平台",
            _ => $"AI{label}识别标注平台",
        };
        ModelScopeText.Text = _kind switch
        {
            AiRecognitionKind.Barcode => "PaddleOCR-VL-1.6 + 条码解析",
            AiRecognitionKind.QrCode => "PaddleOCR-VL-1.6 + QR解析",
            _ => "SOTA · 复杂文档/字符识别",
        };
        AnnotationKindText.Text = $"标注类型：{KindLabel}";
        AnnotationClassesText.Text = KindLabel;
        AnnotationKindOption.Content = KindLabel;
        SaveAnnotationButton.Content = "保存数据集";
        AnnotationDescriptionText.Text = $"为当前图片填写{label}内容，保存后用于训练。";
        TrainingTitleText.Text = $"{label}识别训练";
        TrainingDescriptionText.Text = $"使用{label}标注平台生成的 image/text 数据训练配置；默认基座为 {ModelName}。";
        TrainingHintText.Text = $"提示：{ModelName} 负责视觉文字理解，{label}内容使用专用解析流程进行校验；训练配置会保存到数据集 .visionworkbench/{StorageFolder} 目录。";
        TestDescriptionText.Text = $"{ModelName} 输出的{label}识别结果将在这里展示。";
    }

    private void NewAnnotationDataset_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe) return;
        var root = AppServices.Instance.Settings.DatasetDirectory;
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(root);
        var datasetRoot = Path.Combine(root, $"{StorageFolder}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(datasetRoot);
        LoadAnnotationFolder(datasetRoot);
        AnnotationStatusText.Text = $"已新建数据集目录：{datasetRoot}，请使用“选择图片目录”导入图片。";
    }

    private void LoadAnnotationDataset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "加载字符识别数据集目录", Multiselect = false };
        if (dialog.ShowDialog() == true) LoadAnnotationFolder(dialog.FolderName);
    }

    private void ChooseAnnotationFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        LoadAnnotationFolder(dialog.FolderName);
    }

    private void LoadAnnotationFolder(string folder)
    {
        _annotationRoot = Path.GetFullPath(folder);
        AnnotationDatasetList.Items.Clear();
        AnnotationDatasetList.Items.Add(new DirectoryInfo(_annotationRoot).Name);
        AnnotationDatasetList.SelectedIndex = 0;
        AnnotationDatasetNameText.Text = new DirectoryInfo(_annotationRoot).Name;
        AnnotationSourceRootText.Text = _annotationRoot;
        AnnotationDatasetRootText.Text = _annotationRoot;
        AnnotationRootText.Text = _annotationRoot;
        _selectedAnnotationImage = null;
        AnnotationPreview.Source = null;
        AnnotationText.Clear();
        LoadAnnotations();
        var images = EnumerateImages(_annotationRoot)
            .Select(path => new OcrImageItem(path))
            .ToList();
        AnnotationImageList.ItemsSource = images;
        AnnotationImageListEmptyText.Visibility = images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AnnotationImageList.SelectedIndex = images.Count > 0 ? 0 : -1;
        AnnotationStatusText.Text = $"已加载 {images.Count} 张图片，已标注 {_annotations.Count} 张。";
    }

    private void AutoSplitAnnotationDataset_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_annotationRoot) || !Directory.Exists(_annotationRoot))
        {
            ThemedMessageBox.Show("请先选择数据集目录。", "自动划分数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var images = EnumerateImages(_annotationRoot)
            .Select(path => Path.GetRelativePath(_annotationRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (images.Count == 0)
        {
            ThemedMessageBox.Show("当前数据集没有图片。", "自动划分数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var trainCount = images.Count == 1 ? 1 : Math.Clamp((int)Math.Round(images.Count * 0.8), 1, images.Count - 1);
        var splits = images.Select((path, index) => new
        {
            image = path,
            split = index < trainCount ? "train" : "val"
        }).ToList();
        var splitPath = Path.Combine(_annotationRoot, ".visionworkbench", StorageFolder, "splits.json");
        Directory.CreateDirectory(Path.GetDirectoryName(splitPath)!);
        File.WriteAllText(splitPath, JsonSerializer.Serialize(splits, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        AnnotationStatusText.Text = $"已自动划分数据集：训练集 {trainCount} 张，验证集 {images.Count - trainCount} 张。";
    }

    private void ExportAnnotationDataset_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_annotationRoot) || !Directory.Exists(_annotationRoot))
        {
            ThemedMessageBox.Show("请先选择数据集目录。", "导出数据集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveAnnotations();
        var dialog = new SaveFileDialog
        {
            Filter = "字符标注文件|*.jsonl|所有文件|*.*",
            FileName = $"{StorageFolder}-labels.jsonl",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;
        var source = Path.Combine(_annotationRoot, ".visionworkbench", StorageFolder, "labels.jsonl");
        File.Copy(source, dialog.FileName, overwrite: true);
        AnnotationStatusText.Text = $"数据集已导出：{dialog.FileName}";
    }

    private void AnnotationImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnnotationImageList.SelectedItem is not OcrImageItem image) return;
        _selectedAnnotationImage = image.FullPath;
        AnnotationPreview.Source = LoadBitmap(image.FullPath);
        AnnotationText.Text = _annotations.TryGetValue(image.RelativePath(_annotationRoot), out var text) ? text : "";
        AnnotationStatusText.Text = $"当前图片：{image.FileName}";
    }

    private void SaveAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_annotationRoot) || string.IsNullOrWhiteSpace(_selectedAnnotationImage))
        {
            ThemedMessageBox.Show("请先选择数据集目录和图片。", "保存字符标注", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var relativePath = Path.GetRelativePath(_annotationRoot, _selectedAnnotationImage).Replace('\\', '/');
        _annotations[relativePath] = AnnotationText.Text.Trim();
        SaveAnnotations();
        AnnotationStatusText.Text = $"标注已保存：{relativePath}";
    }

    private void ChooseTrainingFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            TrainingRootText.Text = dialog.FolderName;
            TrainingStatusText.Text = "已选择训练数据集，可以生成配置。";
        }
    }

    private void PrepareTraining_Click(object sender, RoutedEventArgs e)
    {
        var root = TrainingRootText.Text.Trim();
        if (!Directory.Exists(root))
        {
            ThemedMessageBox.Show("请先选择有效的数据集目录。", "生成训练配置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(TrainingEpochText.Text, out var epochs) || epochs <= 0 ||
            !int.TryParse(TrainingBatchText.Text, out var batch) || batch <= 0)
        {
            ThemedMessageBox.Show("训练轮数和批大小必须是大于 0 的整数。", "训练参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var output = Path.Combine(root, ".visionworkbench", StorageFolder);
        Directory.CreateDirectory(output);
        var configPath = Path.Combine(output, $"{StorageFolder}-training.json");
        var config = new
        {
            task = $"{StorageFolder}_recognition",
            model = ModelName,
            datasetRoot = root,
            labels = Path.Combine(output, "labels.jsonl"),
            epochs,
            batchSize = batch,
            createdAt = DateTimeOffset.Now,
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        TrainingStatusText.Text = $"训练配置已生成：{configPath}";
    }

    private void ChooseTestImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.webp", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        _selectedTestImage = dialog.FileName;
        TestImageText.Text = dialog.FileName;
        TestPreview.Source = LoadBitmap(dialog.FileName);
        TestResultText.Text = "";
        TestStatusText.Text = "图片已加载，点击“执行字符识别”。";
    }

    private async void RunOcr_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedTestImage))
        {
            ThemedMessageBox.Show("请先选择一张测试图片。", "字符识别测试", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var python = FindPythonWithOcr(out var worker);
        if (python is null || worker is null)
        {
            TestStatusText.Text = $"当前 Python 环境未安装{RuntimeName}。请先安装 workers/paddleocr-vl/requirements.txt。";
            ThemedMessageBox.Show(
                $"当前环境还没有{RuntimeName}，暂时无法执行识别。\n\n" +
                "请安装 workers/paddleocr-vl/requirements.txt 后重试。字符识别首次运行会自动下载官方模型文件。",
                "字符识别运行时未就绪", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RunOcrButton.IsEnabled = false;
        TestStatusText.Text = $"正在使用 {ModelName} 识别，首次运行可能需要下载模型……";
        try
        {
            var output = Path.Combine(Path.GetTempPath(), "visionworkbench-ocr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            var info = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = Path.GetDirectoryName(worker)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(worker);
            info.ArgumentList.Add(StorageFolder);
            info.ArgumentList.Add(_selectedTestImage);
            info.ArgumentList.Add(output);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动识别 Worker。");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);

            var markdown = Directory.EnumerateFiles(output, "*.md").FirstOrDefault();
            TestResultText.Text = markdown is not null
                ? File.ReadAllText(markdown, Encoding.UTF8)
                : stdout.Trim();
            TestStatusText.Text = "识别完成。";
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"识别失败：{ex.Message}";
            ThemedMessageBox.Show($"字符识别失败：\n{ex.Message}", "字符识别测试", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RunOcrButton.IsEnabled = true;
        }
    }

    private void LoadAnnotations()
    {
        _annotations.Clear();
        if (string.IsNullOrWhiteSpace(_annotationRoot)) return;
        var path = Path.Combine(_annotationRoot, ".visionworkbench", StorageFolder, "labels.jsonl");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            try
            {
                var item = JsonSerializer.Deserialize<OcrLabel>(line);
                if (item is not null && !string.IsNullOrWhiteSpace(item.Image)) _annotations[item.Image] = item.Text ?? "";
            }
            catch (JsonException) { }
        }
    }

    private void SaveAnnotations()
    {
        var path = Path.Combine(_annotationRoot!, ".visionworkbench", StorageFolder, "labels.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = _annotations.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => JsonSerializer.Serialize(new OcrLabel(item.Key, item.Value)));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private string DescribeRuntime()
    {
        var python = FindPythonWithOcr(out _);
        return python is null
            ? $"{ModelName} 已配置为默认模型；本机 {_kind switch { AiRecognitionKind.Barcode => "条码", AiRecognitionKind.QrCode => "二维码", _ => "字符" }} 识别运行时未就绪。安装后可在测试平台直接运行。"
            : $"已发现{RuntimeName}：{python}；默认模型：{ModelName}。";
    }

    private string? FindPythonWithOcr(out string? worker)
    {
        worker = null;
        foreach (var root in CandidateRoots())
        {
            var python = new[]
            {
                Path.Combine(root, "runtime", "python", "python.exe"),
                Path.Combine(root, "workers", ".venv", "Scripts", "python.exe"),
            }.FirstOrDefault(File.Exists);
            var script = Path.Combine(root, "workers", "paddleocr-vl", WorkerFileName);
            if (python is null || !File.Exists(script)) continue;
            var runtimeReady = _kind == AiRecognitionKind.Character ? HasPaddleOcr(python) : HasOpenCv(python);
            if (!runtimeReady) continue;
            worker = script;
            return python;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var starts = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var start in starts)
        {
            var directory = new DirectoryInfo(start);
            for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "workers")))
                    yield return directory.FullName;
                var siblingProject = Path.Combine(directory.FullName, "VisionWorkbench");
                if (Directory.Exists(Path.Combine(siblingProject, "workers")))
                    yield return siblingProject;
            }
        }
    }

    private static bool HasPaddleOcr(string python)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("import paddleocr");
            using var process = Process.Start(info);
            return process is not null && process.WaitForExit(2500) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasOpenCv(string python)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = python, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("import cv2");
            using var process = Process.Start(info);
            return process is not null && process.WaitForExit(2500) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateImages(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private string KindLabel => _kind switch
    {
        AiRecognitionKind.Barcode => "条码",
        AiRecognitionKind.QrCode => "二维码",
        _ => "字符",
    };

    private string StorageFolder => _kind switch
    {
        AiRecognitionKind.Barcode => "barcode",
        AiRecognitionKind.QrCode => "qrcode",
        _ => "ocr",
    };

    private string WorkerFileName => _kind == AiRecognitionKind.Character ? "recognize.py" : "decode_code.py";

    private string RuntimeName => _kind == AiRecognitionKind.Character
        ? $"{ModelName} 运行时"
        : "OpenCV 码制解析运行时";

    private sealed record OcrLabel(string Image, string Text);
    private sealed record OcrImageItem(string FullPath)
    {
        public string FileName => Path.GetFileName(FullPath);
        public string RelativePath(string? root) => root is null ? FileName : Path.GetRelativePath(root, FullPath).Replace('\\', '/');
        public override string ToString() => FileName;
    }
}
