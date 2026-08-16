using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace VisionWorkbench.Infrastructure.Logging;

/// <summary>
/// 日志初始化（文档 §23）：按来源拆分日志文件。
/// application.log / camera.log / algorithm-manager.log / database.log / plugin-{id}.log
/// </summary>
public static class LogSetup
{
    private const long FileSizeLimitBytes = 20 * 1024 * 1024;
    private const int RetainedFileCountLimit = 14;

    public static ILoggerFactory CreateFactory(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => HasSource(e, "VisionWorkbench.Cameras"))
                .WriteTo.File(Combine(logsDirectory, "camera.log"), LogEventLevel.Debug,
                    shared: true, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit))
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e =>
                    HasSource(e, "VisionWorkbench.Algorithms") ||
                    HasSource(e, "VisionWorkbench.Infrastructure"))
                .WriteTo.File(Combine(logsDirectory, "algorithm-manager.log"), LogEventLevel.Debug,
                    shared: true, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit))
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => HasSource(e, "VisionWorkbench.Persistence"))
                .WriteTo.File(Combine(logsDirectory, "database.log"), LogEventLevel.Debug,
                    shared: true, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit))
            .WriteTo.File(Combine(logsDirectory, "application.log"), LogEventLevel.Information,
                shared: true, rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCountLimit)
            .CreateLogger();

        Serilog.Log.Logger = logger;
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(logger);
    }

    /// <summary>插件专属日志（文档 §23 plugin-{id}.log；stderr 输出写这里）。</summary>
    public static Microsoft.Extensions.Logging.ILogger CreatePluginLogger(string logsDirectory, string pluginId)
    {
        Directory.CreateDirectory(logsDirectory);
        var safeId = string.Concat(pluginId.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Combine(logsDirectory, $"plugin-{safeId}.log"), LogEventLevel.Debug,
                shared: true, rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCountLimit)
            .CreateLogger();
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(logger)
            .CreateLogger($"Plugin.{pluginId}");
    }

    public static void CloseAndFlush() => Serilog.Log.CloseAndFlush();

    private static string Combine(string dir, string name) => Path.Combine(dir, name);

    private static bool HasSource(LogEvent e, string prefix) =>
        e.Properties.TryGetValue("SourceContext", out var value) &&
        value.ToString().Trim('"').StartsWith(prefix, StringComparison.Ordinal);
}
