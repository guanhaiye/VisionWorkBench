namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>相机设备描述（跨 Provider 唯一：ProviderId + DeviceId）。</summary>
public sealed record CameraDescriptor
{
    public required string ProviderId { get; init; }
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>稳定标识（序列号）；虚拟源为 null。</summary>
    public string? Serial { get; init; }
}

/// <summary>相机支持的模式。</summary>
public sealed record CameraMode
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
}

/// <summary>相机能力（文档 §10.4）。不支持的控件应隐藏或禁用（CAM-004）。</summary>
public sealed record CameraCapabilities
{
    public IReadOnlyList<CameraMode> SupportedModes { get; init; } = [];
    public bool SupportsExposureControl { get; init; }
    public bool SupportsGainControl { get; init; }
    public bool SupportsTrigger { get; init; }
}

public enum CameraSessionState
{
    Idle,
    Opening,
    Streaming,
    Paused,
    Faulted,
    Closed,
}

/// <summary>相机故障（携带标准错误码，文档 §22.1）。</summary>
public sealed record CameraFault
{
    public required string Code { get; init; }
    public required string Message { get; init; }

    /// <summary>是否值得自动重连（文档 §10.6）。</summary>
    public bool Recoverable { get; init; } = true;
}

/// <summary>相机标准错误码（文档 §22.1）。</summary>
public static class CameraErrorCodes
{
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string DeviceBusy = "DEVICE_BUSY";
    public const string OpenFailed = "OPEN_FAILED";
    public const string StreamStartFailed = "STREAM_START_FAILED";
    public const string FrameTimeout = "FRAME_TIMEOUT";
    public const string DeviceDisconnected = "DEVICE_DISCONNECTED";
    public const string InvalidParameter = "INVALID_PARAMETER";
    public const string SdkNotInstalled = "SDK_NOT_INSTALLED";
    public const string SdkVersionMismatch = "SDK_VERSION_MISMATCH";
}

/// <summary>打开选项。Provider 忽略自己不支持的项（CAM-004）。</summary>
public sealed record CameraOpenOptions
{
    public int? DesiredWidth { get; init; }
    public int? DesiredHeight { get; init; }
    public double? DesiredFps { get; init; }

    /// <summary>虚拟源：帧间隔（毫秒）。0 = 尽快。</summary>
    public int FrameIntervalMs { get; init; }

    /// <summary>虚拟源：是否循环播放。</summary>
    public bool Loop { get; init; }

    /// <summary>虚拟源：最多输出的有效帧数；null 表示不限制。</summary>
    public int? MaxFrames { get; init; }
}

/// <summary>相机参数集合（首版占位，仅接口形状落地）。</summary>
public sealed record CameraParameterSet;

public sealed class VideoFrameReceivedEventArgs : EventArgs
{
    public VideoFrame Frame { get; }

    public VideoFrameReceivedEventArgs(VideoFrame frame) => Frame = frame;
}

public sealed class CameraFaultedEventArgs : EventArgs
{
    public CameraFault Fault { get; }

    public CameraFaultedEventArgs(CameraFault fault) => Fault = fault;
}
