using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Application;

public sealed class DatasetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新数据集";
    public string TaskType { get; set; } = "detection";
    public string RootDirectory { get; set; } = "";
    public List<string> Classes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record DatasetImageItem(
    string FullPath,
    string RelativePath,
    string Split,
    bool HasAnnotation)
{
    public string FileName => Path.GetFileName(RelativePath);
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

    public override string ToString() =>
        $"{ClassName}  {Shape}  ({X:0.000}, {Y:0.000}, {Width:0.000}, {Height:0.000})";
}

public sealed class DatasetPoint
{
    public double X { get; set; }
    public double Y { get; set; }
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
                return new DatasetImageItem(path, relative, SplitFor(relative), File.Exists(annotationPath));
            })
            .ToArray();
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

    public string ExportYolo(DatasetDefinition dataset, string outputDirectory)
    {
        if (dataset.Classes.Count == 0) throw new InvalidOperationException("请先配置至少一个类别");
        var images = ListImages(dataset);
        if (images.Count == 0) throw new InvalidOperationException("数据集目录中没有图片");
        var output = Path.GetFullPath(outputDirectory);
        foreach (var split in new[] { "train", "val" })
        {
            Directory.CreateDirectory(Path.Combine(output, "images", split));
            Directory.CreateDirectory(Path.Combine(output, "labels", split));
        }
        foreach (var image in images)
        {
            var split = image.Split is "val" ? "val" : "train";
            var relativeName = image.RelativePath.Replace('/', '_').Replace('\\', '_');
            var imageDestination = Path.Combine(output, "images", split, relativeName);
            var labelDestination = Path.Combine(output, "labels", split,
                Path.ChangeExtension(relativeName, ".txt"));
            File.Copy(image.FullPath, imageDestination, true);
            var annotation = LoadAnnotation(dataset, image.RelativePath);
            var lines = annotation.Objects.Select(obj => ToYoloLine(obj, dataset.Classes)).OfType<string>();
            File.WriteAllLines(labelDestination, lines);
        }
        var yaml = new StringBuilder()
            .AppendLine($"path: {output.Replace('\\', '/')}")
            .AppendLine("train: images/train")
            .AppendLine("val: images/val")
            .AppendLine("names:");
        for (var i = 0; i < dataset.Classes.Count; i++)
            yaml.AppendLine($"  {i}: {dataset.Classes[i]}");
        File.WriteAllText(Path.Combine(output, "data.yaml"), yaml.ToString(), Encoding.UTF8);
        return output;
    }

    private static string? ToYoloLine(DatasetAnnotationObject obj, IReadOnlyList<string> classes)
    {
        var classIndex = classes.ToList().FindIndex(x => string.Equals(x, obj.ClassName, StringComparison.OrdinalIgnoreCase));
        if (classIndex < 0) return null;
        if (obj.Shape.Equals("polygon", StringComparison.OrdinalIgnoreCase) && obj.Polygon.Count >= 3)
        {
            return $"{classIndex} {string.Join(' ', obj.Polygon.SelectMany(p => new[] { Clamp(p.X), Clamp(p.Y) }).Select(x => x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))}";
        }
        return $"{classIndex} {Clamp(obj.X + obj.Width / 2):0.######} {Clamp(obj.Y + obj.Height / 2):0.######} {Clamp(obj.Width):0.######} {Clamp(obj.Height):0.######}";
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string SplitFor(string relativePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath));
        return hash[0] % 5 == 0 ? "val" : "train";
    }

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
        Id = source.Id, Name = source.Name, TaskType = source.TaskType, RootDirectory = source.RootDirectory,
        Classes = [.. source.Classes], CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt,
    };
}
