using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Threading;

namespace VisionWorkbench.Infrastructure.Logging;

/// <summary>
/// 日志初始化（文档 §23）：按来源拆分日志文件。
/// application.log / camera.log / algorithm-manager.log / database.log / plugin-{id}.log
/// </summary>
public static class LogSetup
{
    private const long FileSizeLimitBytes = 20 * 1024 * 1024;
    private const int RetainedFileCountLimit = 14;

    public static void SetPersistenceLevel(LogPersistenceLevel level) =>
        LogPersistencePolicy.Current = level;

    public static ILoggerFactory CreateFactory(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(e => LogPersistencePolicy.ShouldPersist(e.Level))
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => HasSource(e, "VisionWorkbench.Cameras"))
                .WriteTo.File(Combine(logsDirectory, "camera.log"), LogEventLevel.Verbose,
                    shared: true, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit))
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e =>
                    HasSource(e, "VisionWorkbench.Algorithms") ||
                    HasSource(e, "VisionWorkbench.Infrastructure"))
                .WriteTo.File(Combine(logsDirectory, "algorithm-manager.log"), LogEventLevel.Verbose,
                    shared: true, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit))
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => HasSource(e, "VisionWorkbench.Persistence"))
                .WriteTo.File(Combine(logsDirectory, "database.log"), LogEventLevel.Verbose,
                    shared: true, rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit))
            .WriteTo.File(Combine(logsDirectory, "application.log"), LogEventLevel.Verbose,
                shared: true, rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCountLimit)
            .CreateLogger();

        Serilog.Log.Logger = logger;
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(logger);
    }

    /// <summary>插件专属日志（文档 §23 plugin-{id}.log；stderr 输出写这里）。</summary>
    public static ILoggerFactory CreatePluginLoggerFactory(string logsDirectory, string pluginId)
    {
        Directory.CreateDirectory(logsDirectory);
        var safeId = string.Concat(pluginId.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(e => LogPersistencePolicy.ShouldPersist(e.Level))
            .WriteTo.File(Combine(logsDirectory, $"plugin-{safeId}.log"), LogEventLevel.Verbose,
                shared: true, rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCountLimit)
            .CreateLogger();
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(logger, dispose: true);
    }

    public static void CloseAndFlush() => Serilog.Log.CloseAndFlush();

    private static string Combine(string dir, string name) => Path.Combine(dir, name);

    private static bool HasSource(LogEvent e, string prefix) =>
        e.Properties.TryGetValue("SourceContext", out var value) &&
        value.ToString().Trim('"').StartsWith(prefix, StringComparison.Ordinal);
}

public enum LogPersistenceLevel
{
    All,
    CriticalAndAbove,
    InformationAndAbove,
    WarningAndAbove,
    None,
}

public static class LogPersistencePolicy
{
    private static int _current = (int)LogPersistenceLevel.All;

    public static LogPersistenceLevel Current
    {
        get => (LogPersistenceLevel)Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, (int)value);
    }

    public static bool ShouldPersist(LogEventLevel level) => Current switch
    {
        LogPersistenceLevel.All => level >= LogEventLevel.Verbose,
        LogPersistenceLevel.CriticalAndAbove => level >= LogEventLevel.Fatal,
        LogPersistenceLevel.InformationAndAbove => level >= LogEventLevel.Information,
        LogPersistenceLevel.WarningAndAbove => level >= LogEventLevel.Warning,
        LogPersistenceLevel.None => false,
        _ => true,
    };
}
