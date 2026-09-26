using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Infrastructure.Workers;

public sealed record WorkerProcessOptions
{
    public required string ExecutablePath { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>等待 hello 的超时（文档 §25：Worker 启动 10 秒以内）。</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>initialize → ready（模型加载）超时（文档 §25：30 秒以内）。</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单次请求（submit 等）默认超时。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>shutdown 优雅退出等待，超时后强杀（PLG-011）。</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// 算法 Worker 进程宿主（文档 §13）。
/// stdin/stdout JSON Lines 双向通信；stderr 持续泵读防管道死锁（PLG-008）；
/// 请求按 correlationId 匹配，天然支持乱序返回（PLG-009）。
/// </summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly WorkerProcessOptions _options;
    private readonly ILogger _logger;
    private readonly IDisposable? _loggerLifetime;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolEnvelope<JsonElement>>> _pending = new();
    private readonly TaskCompletionSource<HelloPayload> _hello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _loopsCts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _lifecycleLock = new();
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _shutdownTask;
    private Task? _disposeTask;
    private int _started;
    private int _disposeRequested;
    private Process? _process;
    private long _messageCounter;
    private long _garbageLines;

    public WorkerProcess(WorkerProcessOptions options, ILogger logger, IDisposable? loggerLifetime = null)
    {
        _options = options;
        _logger = logger;
        _loggerLifetime = loggerLifetime;
    }

    /// <summary>Worker 主动推送的事件消息（type=event，无 correlationId）。</summary>
    public event EventHandler<JsonElement>? EventReceived;

    /// <summary>stderr 原始日志行（写插件专属日志）。</summary>
    public event EventHandler<string>? StderrLine;

    /// <summary>进程退出（含崩溃）。ErrorCode = exit code。</summary>
    public event EventHandler<int>? Exited;

    public bool HasExited => _process is null || _process.HasExited;

    public long GarbageLineCount => Interlocked.Read(ref _garbageLines);

    public int PendingRequestCount => _pending.Count;

    /// <summary>启动进程并等待 hello，校验协议版本（文档 §13.4）。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Worker 进程不能重复启动");
        }
        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            Arguments = _options.Arguments,
            WorkingDirectory = _options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // 关键：不能带 BOM，否则 Worker 侧 JSON 解析失败
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        // 双方强制 UTF-8（防 GBK 环境中文乱码）
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        foreach (var (key, value) in _options.ExtraEnvironment)
        {
            psi.EnvironmentVariables[key] = value;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => OnProcessExited();
        try
        {
            _process.Start();
        }
        catch
        {
            _process.Dispose();
            _process = null;
            throw;
        }
        _logger.LogInformation("Worker 进程已启动 PID={Pid}: {Exe} {Args}",
            _process.Id, _options.ExecutablePath, _options.Arguments);

        _stdoutTask = ReadStdoutLoopAsync(_loopsCts.Token);
        _stderrTask = ReadStderrLoopAsync(_loopsCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>启动 → hello 的完整握手。</summary>
    public async Task<HelloPayload> StartAndWaitHelloAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        var hello = await WithTimeoutAsync(_hello.Task, _options.HelloTimeout, "hello", cancellationToken);
        if (!ProtocolVersions.IsSupported(hello.ProtocolVersion))
        {
            throw new AlgorithmFaultException(
                WorkerErrorCodes.ProtocolNotSupported,
                $"Worker 声明协议 {hello.ProtocolVersion}，宿主支持 {string.Join("/", ProtocolVersions.Supported)}");
        }
        return hello;
    }

    /// <summary>启动 → hello → initialize → ready 的完整握手。</summary>
    public async Task<ReadyPayload> StartAndInitializeAsync(
        InitializePayload init, CancellationToken cancellationToken)
    {
        await StartAndWaitHelloAsync(cancellationToken);
        var response = await RequestAsync(
            MessageType.Initialize, init, _options.ReadyTimeout, cancellationToken);
        var ready = ProtocolMessage.DeserializePayload<ReadyPayload>(response.Payload) ?? new ReadyPayload();
        if (response.Type != MessageType.Ready || !ready.Success)
        {
            throw new AlgorithmFaultException(
                ready.ErrorCode ?? WorkerErrorCodes.ModelLoadFailed,
                ready.Error ?? "Worker 初始化失败");
        }
        return ready;
    }

    /// <summary>发送请求并等待带 correlationId 的响应；error 响应转为异常。</summary>
    public async Task<ProtocolEnvelope<JsonElement>> RequestAsync(
        string type, object payload, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var messageId = NextMessageId();
        var completion = new TaskCompletionSource<ProtocolEnvelope<JsonElement>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestTimeout = timeout ?? _options.RequestTimeout;
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(requestTimeout);
        _pending[messageId] = completion;
        try
        {
            await SendMessageAsync(type, messageId, payload, null, requestCancellation.Token)
                .WaitAsync(requestCancellation.Token);
            var envelope = await completion.Task.WaitAsync(requestCancellation.Token);
            if (envelope.Type == MessageType.Error)
            {
                var error = ProtocolMessage.DeserializePayload<ErrorPayload>(envelope.Payload) ?? new();
                throw new AlgorithmFaultException(error.Code, error.Message);
            }
            return envelope;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
            && requestCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"请求 {messageId} 超时（{requestTimeout.TotalSeconds:F0}s）");
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
        }
    }

    /// <summary>写入一行协议消息（加锁防并发交错）。</summary>
    public async Task SendMessageAsync(
        string type, string messageId, object payload, string? correlationId,
        CancellationToken cancellationToken)
    {
        var envelope = new ProtocolEnvelope<object>
        {
            Type = type,
            MessageId = messageId,
            CorrelationId = correlationId,
            Payload = payload,
        };
        var line = ProtocolMessage.Serialize(envelope);

        var writer = _process?.StandardInput;
        if (writer is null || (_process?.HasExited ?? true))
        {
            throw new AlgorithmFaultException(
                WorkerErrorCodes.WorkerTerminated, "Worker 进程已退出，无法发送消息");
        }
        try
        {
            // 只取消排队等待；已开始的 JSON 行必须写完，避免半行与下一请求拼接。
            // 请求方通过 WaitAsync 取消等待，关闭时终止进程可解除堵塞的管道写入。
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line.AsMemory());
                await writer.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new AlgorithmFaultException(
                WorkerErrorCodes.WorkerTerminated, $"stdin 管道已关闭: {ex.Message}");
        }
    }

    /// <summary>优雅关闭：发送 shutdown → 有限等待 → 强杀（PLG-011）。</summary>
    public Task ShutdownAsync()
    {
        lock (_lifecycleLock)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var process = _process;
        try
        {
            if (process is null || process.HasExited) return;
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendMessageAsync(MessageType.Shutdown, NextMessageId(), new { }, null, quick.Token)
                    .WaitAsync(quick.Token);
            }
            catch (Exception)
            {
                // 优雅关闭是尽力而为。
            }

            try
            {
                using var wait = new CancellationTokenSource(_options.ShutdownTimeout);
                await process.WaitForExitAsync(wait.Token);
                _logger.LogInformation("Worker 已优雅退出 PID={Pid}", process.Id);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    _logger.LogWarning("Worker 未响应 shutdown，已强制结束 PID={Pid}", process.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "强制结束 Worker 失败 PID={Pid}", process.Id);
                }
            }
        }
        finally
        {
            // 退出前持续读取 stdout/stderr，否则 Worker 写日志时会填满管道、无法正常退出。
            _loopsCts.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        try
        {
            await ShutdownAsync();
            await Task.WhenAll(_stdoutTask ?? Task.CompletedTask, _stderrTask ?? Task.CompletedTask);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _loopsCts.Dispose();
            _loggerLifetime?.Dispose();
        }
    }

    private async Task ReadStdoutLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _process?.StandardOutput;
        if (reader is null)
        {
            return;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var envelope = ProtocolMessage.Parse(line);
            if (envelope is null)
            {
                Interlocked.Increment(ref _garbageLines);
                _logger.LogWarning(
                    "Worker stdout 非 JSON 行（PLG-007），累计 {Count}: {Line}",
                    Interlocked.Read(ref _garbageLines), Truncate(line));
                continue;
            }

            if (!string.IsNullOrEmpty(envelope.CorrelationId) &&
                _pending.TryRemove(envelope.CorrelationId, out var completion))
            {
                completion.TrySetResult(envelope);
                continue;
            }
            if (envelope.Type == MessageType.Hello)
            {
                try
                {
                    var hello = ProtocolMessage.DeserializePayload<HelloPayload>(envelope.Payload);
                    _hello.TrySetResult(hello ?? new HelloPayload());
                }
                catch (JsonException ex)
                {
                    Interlocked.Increment(ref _garbageLines);
                    _logger.LogWarning(ex, "Worker hello 负载无法解析");
                }
                continue;
            }
            if (envelope.Type == MessageType.Event)
            {
                if (envelope.Payload is { } payload)
                {
                    InvokeHandlers(EventReceived, payload);
                }
                continue;
            }
            if (envelope.Type == MessageType.Error)
            {
                _logger.LogWarning("Worker 未关联错误: {Payload}", envelope.Payload);
                continue;
            }
            _logger.LogDebug("未关联的 Worker 消息 type={Type} id={Id}",
                envelope.Type, envelope.MessageId);
        }
    }

    private async Task ReadStderrLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _process?.StandardError;
        if (reader is null)
        {
            return;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }
            InvokeHandlers(StderrLine, line);
        }
    }

    private void OnProcessExited()
    {
        var code = -1;
        try
        {
            code = _process?.ExitCode ?? -1;
        }
        catch (Exception)
        {
            // 进程尚未完全退出时访问 ExitCode 可能抛错
        }
        _hello.TrySetException(new AlgorithmFaultException(
            WorkerErrorCodes.WorkerTerminated, "Worker 在握手前退出"));
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(new AlgorithmFaultException(
                    WorkerErrorCodes.WorkerTerminated, $"Worker 进程退出（exit code {code}）"));
            }
        }
        _logger.LogWarning("Worker 进程退出 exit={Code}", code);
        InvokeHandlers(Exited, code);
    }

    private void InvokeHandlers<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch (Exception ex) { _logger.LogWarning(ex, "Worker 事件订阅者处理失败"); }
        }
    }

    private string NextMessageId() =>
        $"host-{Interlocked.Increment(ref _messageCounter):000000}";

    private static async Task<T> WithTimeoutAsync<T>(
        Task<T> task, TimeSpan timeout, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"请求 {id} 超时（{timeout.TotalSeconds:F0}s）");
        }
    }

    private static string Truncate(string line) =>
        line.Length <= 200 ? line : line[..200] + "…";
}
