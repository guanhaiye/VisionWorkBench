using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Contracts.Plugins;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Infrastructure.Logging;
using VisionWorkbench.Infrastructure.Plugins;
using VisionWorkbench.Infrastructure.Workers;

namespace VisionWorkbench.Algorithms;

public sealed record AlgorithmManagerOptions
{
    /// <summary>插件扫描根目录（文档 §12）。</summary>
    public required string PluginsRoot { get; init; }

    /// <summary>
    /// 开发态 Python 解释器路径。打包态为 null 时使用插件内嵌 runtime/python.exe
    /// （文档 §12.2），再退回 PATH。
    /// </summary>
    public string? PythonExecutable { get; init; }

    public required string LogsDirectory { get; init; }
    public string HostVersion { get; init; } = "0.1.0";
    public string ExecutionProvider { get; init; } = "cpu";

    /// <summary>启动失败最大重试次数（PLG-006：避免无限重启）。</summary>
    public int MaxStartAttempts { get; init; } = 3;
}

public sealed class PluginFaultedEventArgs(string pluginId, string message) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public string Message { get; } = message;
}

/// <summary>算法插件生命周期管理：扫描、启动、握手、关闭（文档 §5 模块 5）。</summary>
public sealed class AlgorithmManager
{
    private readonly AlgorithmManagerOptions _options;
    private readonly ILogger<AlgorithmManager> _logger;
    private readonly PluginScanner _scanner;
    private readonly List<WorkerAlgorithmSession> _sessions = [];
    private readonly Lock _sessionsLock = new();

    public AlgorithmManager(AlgorithmManagerOptions options, ILogger<AlgorithmManager> logger)
    {
        _options = options;
        _logger = logger;
        _scanner = new PluginScanner(logger);
    }

    public event EventHandler<PluginFaultedEventArgs>? PluginFaulted;

    public IReadOnlyList<DiscoveredPlugin> ScanPlugins() => _scanner.Scan(_options.PluginsRoot);

    /// <summary>启动 Worker 进程并完成 hello 握手（不发送 initialize，由会话负责）。</summary>
    public async Task<WorkerAlgorithmSession> CreateSessionAsync(
        string pluginId, CancellationToken cancellationToken)
    {
        var plugin = ScanPlugins().FirstOrDefault(p =>
            p.Manifest?.Id == pluginId && p.Status == PluginStatus.Valid)
            ?? throw new AlgorithmFaultException(
                WorkerErrorCodes.PluginInvalid,
                $"插件 {pluginId} 不存在、清单非法或协议不兼容（CFG-005）");
        var manifest = plugin.Manifest!;

        var (exe, args) = ResolveCommand(manifest, plugin.Directory);
        _logger.LogInformation("启动算法 Worker: {Plugin} v{Version} → {Exe} {Args}",
            manifest.Id, manifest.Version, exe, args);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= _options.MaxStartAttempts; attempt++)
        {
            var process = new WorkerProcess(
                new WorkerProcessOptions
                {
                    ExecutablePath = exe,
                    Arguments = args,
                    WorkingDirectory = plugin.Directory,
                },
                LogSetup.CreatePluginLogger(_options.LogsDirectory, manifest.Id));
            process.StderrLine += (_, line) =>
                _logger.LogWarning("Worker {Plugin} stderr: {Line}", manifest.Id, line);

            try
            {
                // 只做进程启动 + hello 协议握手；initialize 由会话执行
                await RunHelloHandshakeAsync(process, cancellationToken);
                var session = new WorkerAlgorithmSession(process, manifest, _logger);
                lock (_sessionsLock)
                {
                    _sessions.Add(session);
                }
                return session;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await process.DisposeAsync();
                _logger.LogWarning("Worker 启动第 {Attempt}/{Max} 次失败: {Error}",
                    attempt, _options.MaxStartAttempts, ex.Message);
                if (attempt < _options.MaxStartAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                }
            }
        }

        var fault = lastError switch
        {
            AlgorithmFaultException af => af,
            _ => new AlgorithmFaultException(
                WorkerErrorCodes.RuntimeNotFound, lastError?.Message ?? "未知启动失败"),
        };
        PluginFaulted?.Invoke(this, new PluginFaultedEventArgs(pluginId, fault.Message));
        throw fault;
    }

    /// <summary>启动进程并等待 hello（校验协议版本；initialize 由会话执行）。</summary>
    private static async Task RunHelloHandshakeAsync(
        WorkerProcess process, CancellationToken cancellationToken)
    {
        await process.StartAndWaitHelloAsync(cancellationToken);
    }

    private (string Exe, string Args) ResolveCommand(PluginManifest manifest, string pluginDirectory)
    {
        if (manifest.Runtime.Type.Equals("executable", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(pluginDirectory, manifest.Runtime.Executable ?? "");
            return (exe, manifest.Runtime.Arguments ?? "");
        }

        // python 类型
        var entry = Path.Combine(pluginDirectory, manifest.Runtime.Entry ?? "worker.py");
        // 插件目录内的独立虚拟环境优先，允许 YOLO11 等重型插件携带自己的运行时；
        // 没有插件专属环境时再沿用宿主开发环境或内嵌 runtime。
        var pluginVenv = OperatingSystem.IsWindows()
            ? Path.Combine(pluginDirectory, ".venv", "Scripts", "python.exe")
            : Path.Combine(pluginDirectory, ".venv", "bin", "python");
        // ATU5 uses the same PyTorch/CUDA runtime as the YOLO11 plugin.  In the
        // development layout that heavyweight environment is installed once in
        // workers/yolo11/.venv, while workers/.venv contains only lightweight
        // worker dependencies.  Prefer that shared environment for ATU5 so a
        // semantic task can actually start instead of failing with "torch not found".
        var sharedVisionPython = manifest.Id.Equals("com.vision.atu5", StringComparison.OrdinalIgnoreCase)
            ? (OperatingSystem.IsWindows()
                ? Path.Combine(pluginDirectory, "..", "yolo11", ".venv", "Scripts", "python.exe")
                : Path.Combine(pluginDirectory, "..", "yolo11", ".venv", "bin", "python"))
            : null;
        // 发布态统一使用安装目录内置 runtime，避免客户机上的 Python/开发环境污染运行结果。
        var bundledPython = OperatingSystem.IsWindows()
            ? Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "python", "bin", "python");
        var python = File.Exists(bundledPython) ? bundledPython
            : File.Exists(pluginVenv) ? pluginVenv
            : sharedVisionPython is not null && File.Exists(sharedVisionPython) ? sharedVisionPython
            : _options.PythonExecutable;
        if (string.IsNullOrWhiteSpace(python))
        {
            var bundled = Path.Combine(pluginDirectory, "runtime", "python.exe");
            python = File.Exists(bundled) ? bundled : "python";
        }
        return (python, $"\"{entry}\"");
    }

    /// <summary>应用退出时关闭全部 Worker（PLG-011）。</summary>
    public async Task ShutdownAllAsync()
    {
        WorkerAlgorithmSession[] snapshot;
        lock (_sessionsLock)
        {
            snapshot = [.. _sessions];
            _sessions.Clear();
        }
        foreach (var session in snapshot)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("关闭会话 {Id} 失败: {Error}", session.SessionId, ex.Message);
            }
        }
    }
}
