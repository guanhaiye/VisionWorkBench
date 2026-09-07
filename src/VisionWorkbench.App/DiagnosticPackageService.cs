using System.IO.Compression;
using System.IO;
using System.Text.Json;

namespace VisionWorkbench.App;

/// <summary>诊断包导出（DIA-001）：只收集环境、插件清单和最近日志，不递归打包客户图片。</summary>
public static class DiagnosticPackageService
{
    public static void Export(AppServices services, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        AddText(archive, "environment.json", JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            product = "VisionWorkbench",
            productVersion = typeof(DiagnosticPackageService).Assembly.GetName().Version?.ToString(),
            os = Environment.OSVersion.VersionString,
            framework = Environment.Version.ToString(),
            processArchitecture = Environment.Is64BitProcess ? "x64" : "x86",
            processorCount = Environment.ProcessorCount,
        }, new JsonSerializerOptions { WriteIndented = true }));

        AddText(archive, "settings.json", JsonSerializer.Serialize(new
        {
            services.Settings.ExecutionProvider,
            services.Settings.CurrentRole,
            services.Settings.OperatorName,
            DataDirectory = "<redacted>",
            PythonExecutable = services.Settings.PythonExecutable is null ? null : "<configured>",
            PluginsRoot = services.Settings.PluginsRoot is null ? null : "<configured>",
        }, new JsonSerializerOptions { WriteIndented = true }));

        var plugins = services.AlgorithmManager.ScanPlugins().Select(plugin => new
        {
            id = plugin.Manifest?.Id,
            version = plugin.Manifest?.Version,
            protocolVersion = plugin.Manifest?.ProtocolVersion,
            status = plugin.Status.ToString(),
            errors = plugin.Errors,
        });
        AddText(archive, "plugins.json", JsonSerializer.Serialize(plugins,
            new JsonSerializerOptions { WriteIndented = true }));

        var integrity = services.DatabaseIntegrity.CheckAsync().GetAwaiter().GetResult();
        var auditHealthy = services.Audit.VerifyChainAsync().GetAwaiter().GetResult();
        AddText(archive, "self-check.json", JsonSerializer.Serialize(new
        {
            database = new { integrity.IsHealthy, integrity.Message },
            auditChain = new { IsHealthy = auditHealthy },
            license = new { services.License.Current.State, services.License.Current.IsValid, services.License.Current.IsDevelopment },
        }, new JsonSerializerOptions { WriteIndented = true }));

        var logsDirectory = Path.Combine(services.Settings.DataDirectory, "logs");
        if (Directory.Exists(logsDirectory))
        {
            foreach (var logPath in Directory.EnumerateFiles(logsDirectory, "*.log")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var lines = File.ReadLines(logPath).TakeLast(2000);
                AddText(archive, $"logs/{Path.GetFileName(logPath)}", string.Join(Environment.NewLine, lines));
            }
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
