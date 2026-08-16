using System.Text.Json;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Algorithms;

public enum AlgorithmSessionState
{
    Uninitialized,
    Initializing,
    Ready,
    Running,
    Stopping,
    Stopped,
    Faulted,
}

public sealed record AlgorithmInitialization
{
    /// <summary>算法参数（符合插件 settings.schema.json）。</summary>
    public JsonElement? Settings { get; init; }

    /// <summary>执行后端：cpu / cuda / openvino（文档 §19.4）。</summary>
    public string ExecutionProvider { get; init; } = "cpu";
}

public sealed record AlgorithmStartOptions
{
    /// <summary>snapshot（静态计数）/ stream（实时流）。</summary>
    public string Mode { get; init; } = "snapshot";

    public Contracts.Results.NormalizedRect? Roi { get; init; }
    public double PreferredFps { get; init; }
}

public sealed class AlgorithmOutputEventArgs : EventArgs
{
    public AlgorithmOutput Output { get; }

    public AlgorithmOutputEventArgs(AlgorithmOutput output) => Output = output;
}

public sealed class AlgorithmEventEventArgs : EventArgs
{
    public System.Text.Json.JsonElement Payload { get; }

    public AlgorithmEventEventArgs(System.Text.Json.JsonElement payload) => Payload = payload;
}

public sealed class AlgorithmFaultedEventArgs : EventArgs
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }

    /// <summary>Worker 进程是否已终止（不可恢复，需重启）。</summary>
    public bool Terminated { get; init; }
}

/// <summary>
/// 算法会话（文档 §15）。
/// 实现说明：首版协议为请求-响应式，SubmitAsync 直接返回结果；
/// OutputReceived 事件同时保留，供后续流式 Worker 推送使用。
/// </summary>
public interface IAlgorithmSession : IAsyncDisposable
{
    string SessionId { get; }
    AlgorithmSessionState State { get; }

    Task InitializeAsync(AlgorithmInitialization initialization, CancellationToken cancellationToken);

    Task StartAsync(AlgorithmStartOptions options, CancellationToken cancellationToken);

    Task<AlgorithmOutput> SubmitAsync(AlgorithmInput input, CancellationToken cancellationToken);

    Task FlushAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    event EventHandler<AlgorithmOutputEventArgs>? OutputReceived;
    event EventHandler<AlgorithmEventEventArgs>? EventReceived;
    event EventHandler<AlgorithmFaultedEventArgs>? Faulted;
}
