using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Application;

public sealed class DatasetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新数据集";
    public string TaskType { get; set; } = "detection";
    public string RootDirectory { get; set; } = "";
    public List<string> Classes { get; set; } = [];
    public List<string> KeypointNames { get; set; } = [];
    public Dictionary<string, string> ImageSplits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() => Name;
}

public sealed record DatasetImageItem(
    string FullPath,
    string RelativePath,
    string Split,
    bool HasAnnotation)
{
    public string FileName => Path.GetFileName(RelativePath);
    public string SplitDisplay => Split switch
    {
        "train" => "训",
        "val" => "验",
        _ => "未",
    };
}

public sealed class DatasetAnnotation
{
    public string ImageRelativePath { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public List<DatasetAnnotationObject> Objects { get; set; } = [];
}

public sealed class DatasetAnnotationObject
{
    public string ClassName { get; set; } = "";
    public string Shape { get; set; } = "bbox";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<DatasetPoint> Polygon { get; set; } = [];
    public List<List<DatasetPoint>> PolygonHoles { get; set; } = [];
    public List<DatasetPoint> Keypoints { get; set; } = [];

    public override string ToString() =>
        $"{ClassName}  {Shape}  ({X:0.000}, {Y:0.000}, {Width:0.000}, {Height:0.000})";
}

public sealed class DatasetPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public int Visibility { get; set; } = 2;
}

/// <summary>离线图片数据集目录、标注 sidecar 和 YOLO 导出服务。</summary>
public sealed class DatasetCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp" };
    private readonly string _catalogPath;
    private readonly object _gate = new();
    private List<DatasetDefinition>? _datasets;

    public DatasetCatalogService(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _catalogPath = Path.Combine(dataDirectory, "datasets.json");
    }

    public IReadOnlyList<DatasetDefinition> List()
    {
        lock (_gate)
        {
            return LoadUnsafe().Select(Clone).ToArray();
        }
    }

    public DatasetDefinition Save(DatasetDefinition dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (string.IsNullOrWhiteSpace(dataset.Name))
            throw new ArgumentException("数据集名称不能为空");
        if (string.IsNullOrWhiteSpace(dataset.RootDirectory) || !Directory.Exists(dataset.RootDirectory))
            throw new ArgumentException("图片目录不存在");
        dataset.Name = dataset.Name.Trim();
        dataset.RootDirectory = Path.GetFullPath(dataset.RootDirectory.Trim());
        dataset.Classes = dataset.Classes.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        dataset.KeypointNames = dataset.KeypointNames.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        dataset.ImageSplits = NormalizeSplits(dataset.ImageSplits);
        dataset.UpdatedAt = DateTime.UtcNow;

        lock (_gate)
        {
            var list = LoadUnsafe();
            var index = list.FindIndex(x => x.Id == dataset.Id);
            if (index < 0)
            {
                dataset.CreatedAt = DateTime.UtcNow;
                list.Add(dataset);
            }
            else
            {
                list[index] = dataset;
            }
            WriteUnsafe(list);
            return Clone(dataset);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var list = LoadUnsafe();
            list.RemoveAll(x => x.Id == id);
            WriteUnsafe(list);
        }
    }

    public IReadOnlyList<DatasetImageItem> ListImages(DatasetDefinition dataset)
    {
        if (!Directory.Exists(dataset.RootDirectory)) return [];
        var annotationDirectory = GetAnnotationDirectory(dataset);
        return Directory.EnumerateFiles(dataset.RootDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !IsInternalDatasetPath(Path.GetRelativePath(dataset.RootDirectory, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(dataset.RootDirectory, path);
                var annotationPath = GetAnnotationPath(annotationDirectory, relative);
                return new DatasetImageItem(path, relative, GetSplit(dataset, relative), File.Exists(annotationPath) && LoadAnnotation(dataset, relative).Objects.Count > 0);
            })
            .ToArray();
    }

    public DatasetDefinition SetImageSplit(DatasetDefinition dataset, string relativePath, string split)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (split is not ("train" or "val" or "unassigned"))
            throw new ArgumentException("图片划分状态必须是 train、val 或 unassigned", nameof(split));

        dataset.ImageSplits ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        dataset.ImageSplits[NormalizeRelativePath(relativePath)] = split;
        return Save(dataset);
    }

    public DatasetDefinition AutoSplit(DatasetDefinition dataset, double trainRatio = 0.8)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (trainRatio <= 0 || trainRatio >= 1)
            throw new ArgumentOutOfRangeException(nameof(trainRatio), "训练集比例必须在 0 和 1 之间");

        var images = ListImages(dataset);
        if (images.Count == 0) throw new InvalidOperationException("数据集目录中没有图片");
        var annotatedImages = images.Where(image => image.HasAnnotation).ToArray();
        if (annotatedImages.Length == 0)
            throw new InvalidOperationException("数据集中没有已标注图片，未标注图片不会参与自动划分");

        var trainCount = (int)Math.Round(annotatedImages.Length * trainRatio, MidpointRounding.AwayFromZero);
        if (annotatedImages.Length >= 2) trainCount = Math.Clamp(trainCount, 1, annotatedImages.Length - 1);
        else trainCount = 1;

        dataset.ImageSplits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < annotatedImages.Length; index++)
        {
            dataset.ImageSplits[NormalizeRelativePath(annotatedImages[index].RelativePath)] =
                index < trainCount ? "train" : "val";
        }
        return Save(dataset);
    }

    public DatasetAnnotation LoadAnnotation(DatasetDefinition dataset, string relativePath)
    {
        var path = GetAnnotationPath(GetAnnotationDirectory(dataset), relativePath);
        if (!File.Exists(path)) return new DatasetAnnotation { ImageRelativePath = relativePath };
        try
        {
            return JsonSerializer.Deserialize<DatasetAnnotation>(File.ReadAllText(path), JsonOptions)
                ?? new DatasetAnnotation { ImageRelativePath = relativePath };
        }
        catch (JsonException)
        {
            return new DatasetAnnotation { ImageRelativePath = relativePath };
        }
    }

    public void SaveAnnotation(DatasetDefinition dataset, DatasetAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var directory = GetAnnotationDirectory(dataset);
        Directory.CreateDirectory(directory);
        var path = GetAnnotationPath(directory, annotation.ImageRelativePath);
        File.WriteAllText(path, JsonSerializer.Serialize(annotation, JsonOptions), Encoding.UTF8);
    }

    public string ExportYolo(DatasetDefinition dataset, string outputDirectory, int targetImageSize = 0)
    {
        if (dataset.Classes.Count == 0) throw new InvalidOperationException("请先配置至少一个类别");
        if (string.Equals(dataset.TaskType, "pose", StringComparison.OrdinalIgnoreCase) && dataset.KeypointNames.Count == 0)
            throw new InvalidOperationException("关键点数据集必须配置至少一个关键点");
        var images = ListImages(dataset);
        if (images.Count == 0) throw new InvalidOperationException("数据集目录中没有图片");
        var exportImages = images.Where(x => x.Split is "train" or "val").ToArray();
        if (exportImages.Length == 0)
            throw new InvalidOperationException("请先点击“自动划分数据集”，或通过图片右键菜单设置训练集和验证集");
        if (string.Equals(dataset.TaskType, "pose", StringComparison.OrdinalIgnoreCase))
        {
            var incomplete = exportImages
                .Select(image => (image, annotation: LoadAnnotation(dataset, image.RelativePath)))
                .Where(item => item.annotation.Objects.Any(obj => obj.Keypoints.Count != dataset.KeypointNames.Count))
                .Select(item => item.image.RelativePath)
                .Take(5)
                .ToArray();
            if (incomplete.Length > 0)
                throw new InvalidOperationException($"以下图片存在未完成的关键点标注：{string.Join("、", incomplete)}");
        }
        var output = Path.GetFullPath(outputDirectory);
        foreach (var split in new[] { "train", "val" })
        {
            Directory.CreateDirectory(Path.Combine(output, "images", split));
            Directory.CreateDirectory(Path.Combine(output, "labels", split));
        }
        foreach (var image in exportImages)
        {
            var split = image.Split;
            var relativeName = image.RelativePath.Replace('/', '_').Replace('\\', '_');
            var imageDestination = Path.Combine(output, "images", split, relativeName);
            var labelDestination = Path.Combine(output, "labels", split,
                Path.ChangeExtension(relativeName, ".txt"));
            ExportTrainingImage(image.FullPath, imageDestination, targetImageSize);
            var annotation = LoadAnnotation(dataset, image.RelativePath);
            var lines = annotation.Objects.Select(obj => ToYoloLine(obj, dataset.Classes, dataset.TaskType, dataset.KeypointNames)).OfType<string>();
            File.WriteAllLines(labelDestination, lines);
        }
        var yaml = new StringBuilder()
            .AppendLine($"path: {output.Replace('\\', '/')}")
            .AppendLine("train: images/train")
            .AppendLine("val: images/val");
        if (string.Equals(dataset.TaskType, "pose", StringComparison.OrdinalIgnoreCase))
        {
            if (dataset.KeypointNames.Count == 0)
                throw new InvalidOperationException("关键点数据集必须配置至少一个关键点");
            yaml.AppendLine($"kpt_shape: [{dataset.KeypointNames.Count}, 3]");
        }
        yaml.AppendLine("names:");
        for (var i = 0; i < dataset.Classes.Count; i++)
            yaml.AppendLine($"  {i}: {dataset.Classes[i]}");
        File.WriteAllText(Path.Combine(output, "data.yaml"), yaml.ToString(), Encoding.UTF8);
        return output;
    }

    /// <summary>导出 ATU5/FPN 语义分割 Worker 使用的像素掩膜清单。</summary>
    public string ExportSemantic(DatasetDefinition dataset, string outputDirectory, int targetImageSize = 0)
    {
        if (dataset.Classes.Count == 0) throw new InvalidOperationException("请先配置至少一个类别");
        var images = ListImages(dataset).Where(x => x.Split is "train" or "val" && x.HasAnnotation).ToArray();
        if (images.Length == 0) throw new InvalidOperationException("请先为语义分割图片配置标注并划分训练集和验证集");

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "images", "train"));
        Directory.CreateDirectory(Path.Combine(output, "images", "val"));
        var manifest = new
        {
            classes = dataset.Classes,
            items = images.Select(image => new
            {
                imagePath = ExportSemanticImage(image, output, targetImageSize),
                split = image.Split,
                objects = LoadAnnotation(dataset, image.RelativePath).Objects,
            }),
        };
        var manifestPath = Path.Combine(output, "semantic-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
        return manifestPath;
    }

    private static string ExportSemanticImage(DatasetImageItem image, string outputDirectory, int targetImageSize)
    {
        var relativeName = image.RelativePath.Replace('/', '_').Replace('\\', '_');
        var destination = Path.Combine(outputDirectory, "images", image.Split, relativeName);
        ExportTrainingImage(image.FullPath, destination, targetImageSize);
        return destination;
    }

    private static void ExportTrainingImage(string sourcePath, string destinationPath, int targetImageSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (targetImageSize <= 0)
        {
            File.Copy(sourcePath, destinationPath, true);
            return;
        }

        using var source = Cv2.ImRead(sourcePath, ImreadModes.Color);
        if (source.Empty())
            throw new InvalidOperationException($"无法读取训练图片：{sourcePath}");
        using var resized = new Mat();
        Cv2.Resize(source, resized, new OpenCvSharp.Size(targetImageSize, targetImageSize),
            0, 0, InterpolationFlags.Area);
        if (!Cv2.ImWrite(destinationPath, resized))
            throw new InvalidOperationException($"无法写入训练图片：{destinationPath}");
    }

    private static string? ToYoloLine(DatasetAnnotationObject obj, IReadOnlyList<string> classes,
        string taskType, IReadOnlyList<string> keypointNames)
    {
        var classIndex = classes.ToList().FindIndex(x => string.Equals(x, obj.ClassName, StringComparison.OrdinalIgnoreCase));
        if (classIndex < 0) return null;
        if (string.Equals(taskType, "pose", StringComparison.OrdinalIgnoreCase))
        {
            if (obj.Keypoints.Count != keypointNames.Count || keypointNames.Count == 0) return null;
            var keypoints = obj.Keypoints.Select(point =>
                $"{Clamp(point.X):0.######} {Clamp(point.Y):0.######} {Math.Clamp(point.Visibility, 0, 2)}");
            return $"{classIndex} {Clamp(obj.X + obj.Width / 2):0.######} {Clamp(obj.Y + obj.Height / 2):0.######} {Clamp(obj.Width):0.######} {Clamp(obj.Height):0.######} {string.Join(' ', keypoints)}";
        }
        if (obj.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && obj.Polygon.Count >= 3)
        {
            return $"{classIndex} {string.Join(' ', obj.Polygon.SelectMany(p => new[] { Clamp(p.X), Clamp(p.Y) }).Select(x => x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))}";
        }
        return $"{classIndex} {Clamp(obj.X + obj.Width / 2):0.######} {Clamp(obj.Y + obj.Height / 2):0.######} {Clamp(obj.Width):0.######} {Clamp(obj.Height):0.######}";
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string GetSplit(DatasetDefinition dataset, string relativePath) =>
        dataset.ImageSplits is not null &&
        dataset.ImageSplits.TryGetValue(NormalizeRelativePath(relativePath), out var split) &&
        split is ("train" or "val")
            ? split
            : "unassigned";

    private static Dictionary<string, string> NormalizeSplits(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var item in source)
        {
            if (item.Value is "train" or "val" or "unassigned")
                result[NormalizeRelativePath(item.Key)] = item.Value;
        }
        return result;
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string GetAnnotationDirectory(DatasetDefinition dataset) =>
        Path.Combine(dataset.RootDirectory, ".visionworkbench", "annotations");

    private static bool IsInternalDatasetPath(string relativePath)
    {
        var firstSegment = relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.Equals(firstSegment, ".visionworkbench", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAnnotationPath(string annotationDirectory, string relativePath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath))).ToLowerInvariant();
        return Path.Combine(annotationDirectory, key + ".json");
    }

    private List<DatasetDefinition> LoadUnsafe()
    {
        if (_datasets is not null) return _datasets;
        try
        {
            _datasets = File.Exists(_catalogPath)
                ? JsonSerializer.Deserialize<List<DatasetDefinition>>(File.ReadAllText(_catalogPath), JsonOptions) ?? []
                : [];
        }
        catch (JsonException)
        {
            _datasets = [];
        }
        return _datasets;
    }

    private void WriteUnsafe(List<DatasetDefinition> datasets)
    {
        File.WriteAllText(_catalogPath, JsonSerializer.Serialize(datasets, JsonOptions), Encoding.UTF8);
        _datasets = datasets;
    }

    private static DatasetDefinition Clone(DatasetDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        TaskType = source.TaskType,
        RootDirectory = source.RootDirectory,
        Classes = [.. source.Classes],
        KeypointNames = [.. source.KeypointNames],
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ImageSplits = NormalizeSplits(source.ImageSplits),
    };
}
