using System.Text.Json.Serialization;
using System.Text.Json;

namespace VisionWorkbench.Application;

/// <summary>连续图片序列行为数据集。每个 clip 是一个带行为类别的连续帧片段。</summary>
public sealed class BehaviorDatasetDefinition
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "行为数据集";
    public string RootDirectory { get; set; } = "";
    public int SequenceLength { get; set; } = 30;
    public List<string> Classes { get; set; } = [];
    public List<BehaviorSequenceSource> Sources { get; set; } = [];
    public List<BehaviorClipDefinition> Clips { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>行为数据的原始序列来源：视频，或离线图片序列。</summary>
public sealed class BehaviorSequenceSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "序列";
    public string Kind { get; set; } = "frames";
    public string RelativeVideoPath { get; set; } = "";
    public string RelativeFramesDirectory { get; set; } = "";
    public double FrameRate { get; set; } = 10;
    public int FrameCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    [JsonIgnore]
    public string DisplayName => $"{Name}（{Kind switch { "video" => "视频", _ => "图片序列" }}，{FrameCount}帧）";

    public override string ToString() => DisplayName;
}

public sealed class BehaviorClipDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string SourceId { get; set; } = "";
    public int StartFrame { get; set; }
    public int EndFrame { get; set; }
    public List<string> Frames { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string DisplayName => $"{Label}  ({Frames.Count} 帧)";
}

public static class BehaviorDatasetStore
{
    public const string MetadataFileName = "behavior-dataset.json";
    public const string InternalDirectoryName = ".visionworkbench";
    public const string ModelsDirectoryName = "behavior-models";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string GetMetadataPath(string rootDirectory) =>
        Path.Combine(rootDirectory, InternalDirectoryName, MetadataFileName);

    public static string GetModelsDirectory(string rootDirectory) =>
        Path.Combine(rootDirectory, InternalDirectoryName, ModelsDirectoryName);

    public static BehaviorDatasetDefinition Load(string path)
    {
        var json = File.ReadAllText(path);
        var dataset = System.Text.Json.JsonSerializer.Deserialize<BehaviorDatasetDefinition>(json, JsonOptions)
                      ?? throw new InvalidDataException("行为数据集文件为空或格式不正确。");
        dataset.RootDirectory = Path.GetFullPath(dataset.RootDirectory.Length == 0
            ? Directory.GetParent(Path.GetDirectoryName(path)!)?.FullName ?? Path.GetDirectoryName(path)!
            : dataset.RootDirectory);
        dataset.Classes ??= [];
        dataset.Sources ??= [];
        dataset.Clips ??= [];
        return dataset;
    }

    public static void Save(BehaviorDatasetDefinition dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (string.IsNullOrWhiteSpace(dataset.RootDirectory))
            throw new ArgumentException("行为数据集目录不能为空。", nameof(dataset));
        Directory.CreateDirectory(dataset.RootDirectory);
        var metadataPath = GetMetadataPath(dataset.RootDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        dataset.RootDirectory = Path.GetFullPath(dataset.RootDirectory);
        dataset.SequenceLength = Math.Clamp(dataset.SequenceLength, 8, 300);
        dataset.Classes = dataset.Classes.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        dataset.Clips ??= [];
        dataset.UpdatedAt = DateTime.UtcNow;
        File.WriteAllText(metadataPath, System.Text.Json.JsonSerializer.Serialize(dataset, JsonOptions));
    }

    public static bool TryLoadFromRoot(string rootDirectory, out BehaviorDatasetDefinition? dataset)
    {
        var path = GetMetadataPath(rootDirectory);
        if (!File.Exists(path))
        {
            dataset = null;
            return false;
        }
        try
        {
            dataset = Load(path);
            return true;
        }
        catch (Exception) when (File.Exists(path))
        {
            dataset = null;
            return false;
        }
    }

    public static string NormalizeRelative(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
}
