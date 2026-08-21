using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
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
                results.Add(new DiscoveredPlugin
                {
                    Directory = dir,
                    Status = PluginStatus.InvalidManifest,
                    Errors = ["缺少 plugin.json（PLG-002）"],
                });
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
                results.Add(new DiscoveredPlugin
                {
                    Directory = dir,
                    Status = PluginStatus.InvalidManifest,
                    Errors = [$"无法读取 plugin.json: {ex.Message}"],
                });
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

            var errors = manifest.Validate().ToList();
            errors.AddRange(ValidateRuntimeFiles(manifest, dir));
            errors.AddRange(ValidateHashes(manifest, dir));
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

    private static IReadOnlyList<string> ValidateRuntimeFiles(PluginManifest manifest, string directory)
    {
        var errors = new List<string>();
        var runtimePath = (manifest.Runtime?.Type ?? "").ToLowerInvariant() switch
        {
            "python" => manifest.Runtime?.Entry,
            "executable" => manifest.Runtime?.Executable,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            return errors;
        }

        string fullPath;
        try
        {
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(Path.Combine(directory, runtimePath));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"runtime 路径必须位于插件目录内: {runtimePath}（SEC-001）");
                return errors;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            errors.Add($"runtime 路径无效: {runtimePath}（SEC-001）");
            return errors;
        }

        if (!File.Exists(fullPath))
        {
            errors.Add($"runtime 入口文件不存在: {runtimePath}（PLG-012）");
        }
        return errors;
    }

    private static IReadOnlyList<string> ValidateHashes(PluginManifest manifest, string directory)
    {
        var errors = new List<string>();
        foreach (var (relativePath, expected) in manifest.FileHashes ?? new Dictionary<string, string>())
        {
            string path;
            string root;
            try
            {
                root = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                path = Path.GetFullPath(Path.Combine(directory, relativePath));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                errors.Add($"哈希文件路径无效: {relativePath}（SEC-001）");
                continue;
            }
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"哈希文件必须位于插件目录内: {relativePath}（SEC-001）");
                continue;
            }
            if (!File.Exists(path))
            {
                errors.Add($"哈希文件不存在: {relativePath}（SEC-002）");
                continue;
            }
            try
            {
                using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"文件哈希不匹配: {relativePath}（SEC-002）");
                }
            }
            catch (IOException ex)
            {
                errors.Add($"读取哈希文件失败: {relativePath}: {ex.Message}（SEC-002）");
            }
        }
        return errors;
    }
}
