namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>相机会话（文档 §10.4）。</summary>
public interface ICameraSession : IAsyncDisposable
{
    CameraDescriptor Descriptor { get; }
    CameraSessionState State { get; }
    CameraCapabilities Capabilities { get; }

    Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    Task ApplyParametersAsync(
        CameraParameterSet parameters,
        CancellationToken cancellationToken);

    event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    event EventHandler<CameraFaultedEventArgs>? Faulted;

    /// <summary>有限输入源（图片目录/视频文件）播放完毕时触发；实时源不触发。</summary>
    event EventHandler? Completed;
}
