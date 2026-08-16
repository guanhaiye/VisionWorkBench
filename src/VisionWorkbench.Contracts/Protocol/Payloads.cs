using System.Text.Json;

namespace VisionWorkbench.Contracts.Protocol;

/// <summary>Worker 启动后主动发送的第一条消息（文档 §13.4 生命周期）。</summary>
public sealed record HelloPayload
{
    public string PluginId { get; init; } = "";
    public string WorkerVersion { get; init; } = "";
    public string ProtocolVersion { get; init; } = "";
}

/// <summary>宿主 → Worker：初始化并加载模型（模型只加载一次）。</summary>
public sealed record InitializePayload
{
    /// <summary>执行后端偏好：cpu / cuda / openvino 等（文档 §19.4）。</summary>
    public string ExecutionProvider { get; init; } = "cpu";

    /// <summary>算法参数（符合插件 settings.schema.json 的对象）。</summary>
    public JsonElement? Settings { get; init; }

    public string HostVersion { get; init; } = "";
}

/// <summary>Worker → 宿主：初始化完成，可创建会话。</summary>
public sealed record ReadyPayload
{
    public bool Success { get; init; }
    public string? LoadedModel { get; init; }
    public string? Device { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>协议错误负载（文档 §22.2 标准错误码）。</summary>
public sealed record ErrorPayload
{
    public string Code { get; init; } = WorkerErrorCodes.Unknown;
    public string Message { get; init; } = "";
    public string? Details { get; init; }
}

/// <summary>算法标准错误码（文档 §22.2）。相机错误码见 Cameras.Abstractions。</summary>
public static class WorkerErrorCodes
{
    public const string PluginInvalid = "PLUGIN_INVALID";
    public const string ProtocolNotSupported = "PROTOCOL_NOT_SUPPORTED";
    public const string RuntimeNotFound = "RUNTIME_NOT_FOUND";
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    public const string ModelLoadFailed = "MODEL_LOAD_FAILED";
    public const string InputInvalid = "INPUT_INVALID";
    public const string ImageDecodeFailed = "IMAGE_DECODE_FAILED";
    public const string GpuNotAvailable = "GPU_NOT_AVAILABLE";
    public const string GpuOutOfMemory = "GPU_OUT_OF_MEMORY";
    public const string InferenceTimeout = "INFERENCE_TIMEOUT";
    public const string InferenceFailed = "INFERENCE_FAILED";
    public const string WorkerTerminated = "WORKER_TERMINATED";
    public const string SettingsInvalid = "SETTINGS_INVALID";

    public const string Unknown = "UNKNOWN";
    public const string HostTimeout = "HOST_TIMEOUT";
    public const string ProtocolGarbage = "PROTOCOL_GARBAGE_OUTPUT";
}
