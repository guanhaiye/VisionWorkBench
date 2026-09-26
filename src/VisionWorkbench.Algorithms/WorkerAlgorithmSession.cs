using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Contracts.Plugins;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Infrastructure.Workers;

namespace VisionWorkbench.Algorithms;

/// <summary>基于 WorkerProcess 的算法会话实现。</summary>
public sealed class WorkerAlgorithmSession : IAlgorithmSession
{
    private readonly WorkerProcess _process;
    private readonly PluginManifest _manifest;
    private readonly ILogger _logger;
    private int _disposeRequested;

    public WorkerAlgorithmSession(WorkerProcess process, PluginManifest manifest, ILogger logger)
    {
        _process = process;
        _manifest = manifest;
        _logger = logger;
        _process.EventReceived += (_, payload) => EventReceived?.Invoke(this, new AlgorithmEventEventArgs(payload));
        _process.Exited += (_, code) => OnWorkerExited(code);
    }

    public event EventHandler<AlgorithmOutputEventArgs>? OutputReceived;
    public event EventHandler<AlgorithmEventEventArgs>? EventReceived;
    public event EventHandler<AlgorithmFaultedEventArgs>? Faulted;

    public string SessionId { get; } = $"sess-{Guid.NewGuid():N}";
    public string PluginId => _manifest.Id;

    public AlgorithmSessionState State { get; private set; } = AlgorithmSessionState.Uninitialized;

    public bool IsAlive => !_process.HasExited;

    /// <summary>initialize → ready（模型只加载一次，文档 §13.1）。</summary>
    public async Task InitializeAsync(AlgorithmInitialization initialization, CancellationToken cancellationToken)
    {
        if (State != AlgorithmSessionState.Uninitialized)
        {
            throw new InvalidOperationException($"会话状态 {State} 下不能初始化");
        }
        State = AlgorithmSessionState.Initializing;
        try
        {
            var payload = new
            {
                executionProvider = initialization.ExecutionProvider,
                settings = initialization.Settings,
            };
            var response = await _process.RequestAsync(
                MessageType.Initialize, payload,
                timeout: TimeSpan.FromSeconds(30), cancellationToken);
            var ready = ProtocolMessage.DeserializePayload<ReadyPayload>(response.Payload);
            if (response.Type != MessageType.Ready || ready?.Success != true)
            {
                throw new AlgorithmFaultException(
                    ready?.ErrorCode ?? WorkerErrorCodes.ModelLoadFailed,
                    ready?.Error ?? "Worker 初始化失败或未返回 ready 响应");
            }
            State = AlgorithmSessionState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = AlgorithmSessionState.Faulted;
            throw;
        }
        catch (Exception ex)
        {
            State = AlgorithmSessionState.Faulted;
            throw Translate(ex);
        }
    }

    public async Task StartAsync(AlgorithmStartOptions options, CancellationToken cancellationToken)
    {
        EnsureReady();
        var payload = new
        {
            sessionId = SessionId,
            mode = options.Mode,
            roi = options.Roi,
            preferredFps = options.PreferredFps > 0 ? options.PreferredFps : (double?)null,
        };
        await _process.RequestAsync(MessageType.StartSession, payload, null, cancellationToken);
        State = AlgorithmSessionState.Running;
    }

    public async Task<AlgorithmOutput> SubmitAsync(AlgorithmInput input, CancellationToken cancellationToken)
    {
        if (State is not (AlgorithmSessionState.Running or AlgorithmSessionState.Ready))
        {
            throw new InvalidOperationException($"会话状态 {State} 下不能提交输入");
        }
        var payload = new
        {
            inputId = input.InputId,
            imagePath = input.ImagePath,
            frameSequence = input.FrameSequence,
            capturedAt = input.CapturedAt,
            roi = input.Roi,
        };
        try
        {
            var response = await _process.RequestAsync(
                MessageType.Submit, payload, timeout: TimeSpan.FromSeconds(60), cancellationToken);
            var output = ProtocolMessage.DeserializePayload<AlgorithmOutput>(response.Payload)
                ?? throw new AlgorithmFaultException(
                    WorkerErrorCodes.InferenceFailed, "Worker 返回的 result 负载无法解析");
            OutputReceived?.Invoke(this, new AlgorithmOutputEventArgs(output));
            return output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not AlgorithmFaultException)
        {
            State = AlgorithmSessionState.Faulted;
            throw Translate(ex);
        }
    }

    public async Task SendCommandAsync(string messageType, object payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new ArgumentException("消息类型不能为空", nameof(messageType));
        }
        EnsureReady();
        // 使用进程默认请求超时，避免失去响应的 Worker 挂住控制命令。
        await _process.RequestAsync(messageType, payload, null, cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (State != AlgorithmSessionState.Running)
        {
            return;
        }
        await _process.RequestAsync(MessageType.Flush, new { ack = true }, null, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (State is not (AlgorithmSessionState.Running or AlgorithmSessionState.Ready))
        {
            State = AlgorithmSessionState.Stopped;
            return;
        }
        State = AlgorithmSessionState.Stopping;
        try
        {
            await _process.RequestAsync(
                MessageType.StopSession, new { sessionId = SessionId }, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("stop_session 失败（尽力而为）: {Error}", ex.Message);
        }
        // stop_session 只结束本次检测，不卸载 Worker 中已初始化的模型；保留 Ready
        // 状态，后续开始检测可直接复用，不必再次加载模型。
        State = _process.HasExited ? AlgorithmSessionState.Faulted : AlgorithmSessionState.Ready;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        if (State is AlgorithmSessionState.Running or AlgorithmSessionState.Ready)
        {
            await StopAsync(CancellationToken.None);
        }
        await _process.DisposeAsync();
        State = AlgorithmSessionState.Stopped;
    }

    private void EnsureReady()
    {
        if (State is not (AlgorithmSessionState.Ready or AlgorithmSessionState.Running))
        {
            throw new InvalidOperationException($"会话未就绪（当前 {State}）");
        }
    }

    private void OnWorkerExited(int code)
    {
        if (Volatile.Read(ref _disposeRequested) != 0
            || State is AlgorithmSessionState.Stopping or AlgorithmSessionState.Stopped)
        {
            return;
        }
        if (State is AlgorithmSessionState.Running or AlgorithmSessionState.Ready)
        {
            State = AlgorithmSessionState.Faulted;
        }
        Faulted?.Invoke(this, new AlgorithmFaultedEventArgs
        {
            ErrorCode = WorkerErrorCodes.WorkerTerminated,
            Message = $"算法进程退出（exit code {code}）",
            Terminated = true,
        });
    }

    private static AlgorithmFaultException Translate(Exception ex) => ex switch
    {
        AlgorithmFaultException fault => fault,
        TimeoutException timeout => new AlgorithmFaultException(
            WorkerErrorCodes.InferenceTimeout, timeout.Message),
        _ => new AlgorithmFaultException(
            WorkerErrorCodes.InferenceFailed, ex.Message),
    };
}
