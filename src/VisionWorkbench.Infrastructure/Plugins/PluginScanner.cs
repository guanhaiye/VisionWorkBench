using Microsoft.Extensions.Logging;
using VisionWorkbench.Contracts.Plugins;
using VisionWorkbench.Contracts.Protocol;

namespace VisionWorkbench.Infrastructure.Plugins;

/// <summary>插件目录扫描（文档 §12.3）：发现、解析清单、协议兼容性检查。</summary>
public sealed class PluginScanner(ILogger logger)
{
    public IReadOnlyList<DiscoveredPlugin> Scan(string pluginsRoot)
    {
        var results = new List<DiscoveredPlugin>();
        if (!Directory.Exists(pluginsRoot))
        {
            logger.LogWarning("插件目录不存在: {Root}", pluginsRoot);
            return results;
        }

        foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                logger.LogWarning("目录缺少 plugin.json，跳过（PLG-002）: {Dir}", dir);
                continue;
            }

            PluginManifest? manifest;
            try
            {
                manifest = PluginManifest.TryParse(File.ReadAllText(manifestPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("读取插件清单失败: {Path} ({Error})", manifestPath, ex.Message);
                continue;
            }

            if (manifest is null)
            {
                logger.LogError("plugin.json 解析失败: {Path}", manifestPath);
                results.Add(new DiscoveredPlugin
                {
                    Directory = dir,
                    Status = PluginStatus.InvalidManifest,
                    Errors = ["plugin.json 不是合法 JSON 或字段类型错误"],
                });
                continue;
            }

            if (!ProtocolVersions.IsSupported(manifest.ProtocolVersion))
            {
                logger.LogError("插件协议不兼容（PLG-003）: {Id} 声明 {Version}，宿主支持 {Supported}",
                    manifest.Id, manifest.ProtocolVersion, string.Join("/", ProtocolVersions.Supported));
                results.Add(new DiscoveredPlugin
                {
                    Manifest = manifest,
                    Directory = dir,
                    Status = PluginStatus.IncompatibleProtocol,
                    Errors =
                    [
                        $"插件声明协议 {manifest.ProtocolVersion}，宿主支持 {string.Join("/", ProtocolVersions.Supported)}",
                    ],
                });
                continue;
            }

            var errors = manifest.Validate();
            results.Add(new DiscoveredPlugin
            {
                Manifest = manifest,
                Directory = dir,
                Status = errors.Count == 0 ? PluginStatus.Valid : PluginStatus.InvalidManifest,
                Errors = errors,
            });
        }

        return results;
    }
}
