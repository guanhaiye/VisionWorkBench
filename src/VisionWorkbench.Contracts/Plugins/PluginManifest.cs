using System.Text.Json;
using VisionWorkbench.Contracts.Protocol;

namespace VisionWorkbench.Contracts.Plugins;

/// <summary>插件运行时描述（文档 §12.2）。</summary>
public sealed record PluginRuntime
{
    /// <summary>python / executable。</summary>
    public string Type { get; init; } = "python";

    /// <summary>打包态随插件交付的解释器相对路径（如 runtime/python.exe）。</summary>
    public string? Executable { get; init; }

    /// <summary>入口脚本相对路径（python 类型必填）。</summary>
    public string? Entry { get; init; }

    public string? Arguments { get; init; }
}

public sealed record PluginCapabilities
{
    public IReadOnlyList<string> InputModes { get; init; } = ["single-image"];
    public IReadOnlyList<string> Outputs { get; init; } = ["detection"];
    public bool Stateful { get; init; }
    public bool Realtime { get; init; }
}

public sealed record PluginStreaming
{
    public int PreferredFps { get; init; } = 10;
    public string QueuePolicy { get; init; } = "latest";
    public int MaximumQueueLength { get; init; } = 3;
}

/// <summary>算法插件清单（文档 §12.2 plugin.json）。</summary>
public sealed record PluginManifest
{
    public string ManifestVersion { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string ProtocolVersion { get; init; } = "";
    public string? MinimumHostVersion { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public PluginRuntime Runtime { get; init; } = new();
    public PluginCapabilities Capabilities { get; init; } = new();
    public PluginStreaming Streaming { get; init; } = new();
    public IReadOnlyList<string> ExecutionProviders { get; init; } = ["cpu"];
    public string? SettingsSchema { get; init; }

    /// <summary>清单静态校验（PLG-002 / PLG-003）。返回错误列表；空列表表示通过。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ManifestVersion != "1.0")
        {
            errors.Add($"manifestVersion 必须为 1.0，当前为 “{ManifestVersion}”");
        }
        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("id 不能为空");
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("name 不能为空");
        }
        if (string.IsNullOrWhiteSpace(Version))
        {
            errors.Add("version 不能为空");
        }
        if (!ProtocolVersions.IsSupported(ProtocolVersion))
        {
            errors.Add(
                $"protocolVersion “{ProtocolVersion}” 与宿主不兼容（宿主支持：" +
                $"{string.Join("/", ProtocolVersions.Supported)}）");
        }
        if (string.IsNullOrWhiteSpace(Runtime?.Type))
        {
            errors.Add("runtime.type 不能为空");
        }
        else if (Runtime.Type == "python" && string.IsNullOrWhiteSpace(Runtime.Entry))
        {
            errors.Add("python 类型插件必须提供 runtime.entry");
        }
        return errors;
    }

    public static PluginManifest? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(json, ProtocolMessage.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public enum PluginStatus
{
    /// <summary>清单合法且协议兼容，可启动。</summary>
    Valid,

    /// <summary>缺少 plugin.json 或清单解析/校验失败（PLG-002）。</summary>
    InvalidManifest,

    /// <summary>协议版本不兼容（PLG-003）。</summary>
    IncompatibleProtocol,
}

/// <summary>插件扫描结果（供算法管理与 UI 展示）。</summary>
public sealed record DiscoveredPlugin
{
    /// <summary>清单解析失败时为 null（Status = InvalidManifest）。</summary>
    public PluginManifest? Manifest { get; init; }
    public required string Directory { get; init; }
    public PluginStatus Status { get; init; } = PluginStatus.Valid;
    public IReadOnlyList<string> Errors { get; init; } = [];
}
