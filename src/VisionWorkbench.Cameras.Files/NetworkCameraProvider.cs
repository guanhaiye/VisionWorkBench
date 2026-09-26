using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Cameras.Files;

/// <summary>网络相机输入（RTSP/HTTP 等网络视频流）。</summary>
public sealed class NetworkCameraProvider(ILogger? logger = null) : ICameraProvider
{
    public const string ProviderIdValue = "network";

    public string ProviderId => ProviderIdValue;
    public string DisplayName => "网络相机";

    // 网络相机通常通过固定地址或厂商 SDK 发现；地址模式不做盲目扫描。
    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);

    public Task<ICameraSession> CreateSessionAsync(
        CameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        var session = new NetworkCameraSession(descriptor, logger);
        return Task.FromResult<ICameraSession>(session);
    }
}

internal sealed class NetworkCameraSession(
    CameraDescriptor descriptor, ILogger? logger) : ICameraSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly AsyncPauseGate _pauseGate = new();
    private readonly object _lifecycleGate = new();
    private Task? _disposeTask;
    private Task? _loopTask;
    private VideoCapture? _capture;
    private long _sequence;
    private double _fps = 25;

    public CameraDescriptor Descriptor { get; } = descriptor;
    public CameraSessionState State { get; private set; } = CameraSessionState.Idle;
    public CameraCapabilities Capabilities { get; private set; } = new();

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<CameraFaultedEventArgs>? Faulted;
    public event EventHandler? Completed;

    public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_capture is not null || _loopTask is not null)
            {
                throw new InvalidOperationException("相机会话已打开，请创建新会话重新打开设备");
            }
            if (!IsSupportedAddress(Descriptor.DeviceId))
            {
                Fault(CameraErrorCodes.InvalidParameter,
                    "网络相机地址无效，请填写 rtsp://、rtsps://、http:// 或 https:// 地址",
                    recoverable: false);
                return Task.CompletedTask;
            }

            try
            {
                _capture = new VideoCapture(Descriptor.DeviceId);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "网络相机打开异常: {Address}", Descriptor.DeviceId);
            }

            if (_capture is null || !_capture.IsOpened())
            {
                _capture?.Dispose();
                _capture = null;
                Fault(CameraErrorCodes.OpenFailed,
                    $"网络相机无法打开，请检查地址、网络连通性和账号密码: {Descriptor.DeviceId}",
                    recoverable: true);
                return Task.CompletedTask;
            }

            _fps = options.DesiredFps ?? (_capture.Fps > 0 ? _capture.Fps : 25);
            Capabilities = new CameraCapabilities
            {
                SupportedModes =
                [
                    new CameraMode
                    {
                        Width = (int)_capture.Get(VideoCaptureProperties.FrameWidth),
                        Height = (int)_capture.Get(VideoCaptureProperties.FrameHeight),
                        Fps = _fps,
                    },
                ],
            };
            State = CameraSessionState.Idle;
            return Task.CompletedTask;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (State == CameraSessionState.Streaming || _loopTask is not null)
            {
                return Task.CompletedTask;
            }
            if (_capture is null)
            {
                Fault(CameraErrorCodes.OpenFailed, "网络相机会话未打开", recoverable: false);
                return Task.CompletedTask;
            }
            State = CameraSessionState.Streaming;
            _loopTask = Task.Run(() => RunAsync(_capture, _cts.Token));
            return Task.CompletedTask;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (State == CameraSessionState.Streaming)
            {
                _pauseGate.Pause();
                State = CameraSessionState.Paused;
            }
            return Task.CompletedTask;
        }
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (State == CameraSessionState.Paused)
            {
                _pauseGate.Resume();
                State = CameraSessionState.Streaming;
            }
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return Task.CompletedTask;
            _cts.Cancel();
            if (State == CameraSessionState.Streaming)
            {
                State = CameraSessionState.Paused;
            }
            return Task.CompletedTask;
        }
    }

    public Task ApplyParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _cts.Cancel();
        State = CameraSessionState.Closed;
        var released = await CameraSessionCleanup.ReleaseAfterLoopAsync(_loopTask, () =>
        {
            try
            {
                _capture?.Dispose();
            }
            finally
            {
                _capture = null;
                _pauseGate.Dispose();
                _cts.Dispose();
            }
        }, TimeSpan.FromSeconds(3), ex => logger?.LogWarning(ex, "关闭相机会话时清理资源失败"));
        if (!released) logger?.LogWarning("采集线程仍未退出，原生资源将在采集结束后释放: {Device}", Descriptor.DeviceId);
    }

    private async Task RunAsync(VideoCapture capture, CancellationToken ct)
    {
        using var mat = new Mat();
        var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _fps));
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await _pauseGate.WaitIfPausedAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            if (ct.IsCancellationRequested) break;
            var readOk = false;
            try { readOk = capture.Read(mat); }
            catch (Exception ex) { logger?.LogWarning(ex, "读取网络相机帧失败"); }

            if (ct.IsCancellationRequested) break;
            if (!readOk || mat.Empty())
            {
                if (++consecutiveFailures >= 5)
                {
                    Fault(CameraErrorCodes.FrameTimeout, "网络相机连续无法读取视频帧，请检查网络连接", recoverable: true);
                    break;
                }
                await DelaySafe(TimeSpan.FromMilliseconds(200), ct);
                continue;
            }
            consecutiveFailures = 0;
            var frame = FrameConvert.ToFrame(mat, Interlocked.Increment(ref _sequence));
            if (frame is not null) FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(frame));
            await DelaySafe(interval, ct);
        }
        if (State == CameraSessionState.Streaming) State = CameraSessionState.Idle;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSupportedAddress(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme is "rtsp" or "rtsps" or "http" or "https";
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (TaskCanceledException) { }
    }

    private void Fault(string code, string message, bool recoverable)
    {
        State = CameraSessionState.Faulted;
        Faulted?.Invoke(this, new CameraFaultedEventArgs(new CameraFault
        {
            Code = code,
            Message = message,
            Recoverable = recoverable,
        }));
    }
}
