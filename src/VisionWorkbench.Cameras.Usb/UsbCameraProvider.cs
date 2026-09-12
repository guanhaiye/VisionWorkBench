using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Cameras.Usb;

/// <summary>USB 摄像头（DirectShow 后端）：索引探测发现，帧事件推送（CAM-001/003）。</summary>
public sealed class UsbCameraProvider(ILogger? logger = null) : ICameraProvider
{
    public const string ProviderIdValue = "usb";
    private const int MaxProbeIndex = 7;
    private const string VideoCaptureDeviceClassPath =
        @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{e5323777-f976-4f5b-9b55-b94699c46e44}";

    public string ProviderId => ProviderIdValue;
    public string DisplayName => "USB 相机";

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var found = new List<CameraDescriptor>();
        var deviceCount = OperatingSystem.IsWindows()
            ? TryGetVideoCaptureDeviceCount()
            : null;
        for (var i = 0; i <= MaxProbeIndex; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var probe = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
            if (!probe.IsOpened())
            {
                continue;
            }
            // IsOpened 在 DSHOW 下可能误报，Grab 确认真实可用
            using var mat = new Mat();
            if (!probe.Grab())
            {
                continue;
            }
            var mode = new CameraMode
            {
                Width = (int)probe.Get(VideoCaptureProperties.FrameWidth),
                Height = (int)probe.Get(VideoCaptureProperties.FrameHeight),
                Fps = probe.Get(VideoCaptureProperties.Fps),
            };
            found.Add(new CameraDescriptor
            {
                ProviderId = ProviderIdValue,
                DeviceId = i.ToString(),
                DisplayName = $"USB 相机 {i}",
            });
            logger?.LogDebug("发现 USB 相机: index={Index} {Mode}", i, mode);

            // DirectShow/OpenCV 偶尔会为同一个物理设备暴露多个可打开的索引。
            // Windows 的视频采集设备接口数量才是设备数量，达到该数量后停止发布别名。
            if (deviceCount is > 0 && found.Count >= deviceCount.Value)
            {
                break;
            }
        }
        return Task.FromResult<IReadOnlyList<CameraDescriptor>>(found);
    }

    [SupportedOSPlatform("windows")]
    private static int? TryGetVideoCaptureDeviceCount()
    {
        try
        {
            using var deviceClass = Registry.LocalMachine.OpenSubKey(VideoCaptureDeviceClassPath);
            var count = deviceClass?.GetSubKeyNames().Length ?? 0;
            return count > 0 ? count : null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task<ICameraSession> CreateSessionAsync(
        CameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!int.TryParse(descriptor.DeviceId, out _))
        {
            throw new ArgumentException($"USB 相机 DeviceId 应为索引数字: {descriptor.DeviceId}", nameof(descriptor));
        }
        var session = new UsbCameraSession(descriptor, logger);
        return Task.FromResult<ICameraSession>(session);
    }
}

internal sealed class UsbCameraSession(
    CameraDescriptor descriptor, ILogger? logger) : ICameraSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly AsyncPauseGate _pauseGate = new();
    private Task? _loopTask;
    private VideoCapture? _capture;
    private long _sequence;
    private double _desiredFps;

    public CameraDescriptor Descriptor { get; } = descriptor;
    public CameraSessionState State { get; private set; } = CameraSessionState.Idle;
    public CameraCapabilities Capabilities { get; private set; } = new();

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<CameraFaultedEventArgs>? Faulted;
    public event EventHandler? Completed;

    public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken)
    {
        var index = int.Parse(Descriptor.DeviceId);
        var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened() || !capture.Grab())
        {
            capture.Dispose();
            Fault(CameraErrorCodes.OpenFailed, $"USB 相机打开失败（索引 {index}，可能被占用或已拔出）", recoverable: true);
            return Task.CompletedTask;
        }
        if (options.DesiredWidth is { } w && w > 0)
        {
            capture.Set(VideoCaptureProperties.FrameWidth, w);
        }
        if (options.DesiredHeight is { } h && h > 0)
        {
            capture.Set(VideoCaptureProperties.FrameHeight, h);
        }
        _desiredFps = options.DesiredFps ?? 0;
        if (_desiredFps > 0)
        {
            capture.Set(VideoCaptureProperties.Fps, _desiredFps);
        }
        _capture = capture;
        Capabilities = new CameraCapabilities
        {
            SupportedModes =
            [
                new CameraMode
                {
                    Width = (int)capture.Get(VideoCaptureProperties.FrameWidth),
                    Height = (int)capture.Get(VideoCaptureProperties.FrameHeight),
                    Fps = capture.Get(VideoCaptureProperties.Fps),
                },
            ],
        };
        State = CameraSessionState.Idle;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (State == CameraSessionState.Streaming || _loopTask is not null)
        {
            return Task.CompletedTask;
        }
        if (_capture is null)
        {
            Fault(CameraErrorCodes.OpenFailed, "会话未打开", recoverable: false);
            return Task.CompletedTask;
        }
        State = CameraSessionState.Streaming;
        _loopTask = Task.Run(() => RunAsync(_capture, _cts.Token));
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        if (State == CameraSessionState.Streaming)
        {
            _pauseGate.Pause();
            State = CameraSessionState.Paused;
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (State == CameraSessionState.Paused)
        {
            _pauseGate.Resume();
            State = CameraSessionState.Streaming;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (State == CameraSessionState.Streaming)
        {
            State = CameraSessionState.Paused;
        }
        return Task.CompletedTask;
    }

    public Task ApplyParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 采集线程自行退出
            }
        }
        _capture?.Dispose();
        _capture = null;
        _cts.Dispose();
        _pauseGate.Dispose();
        State = CameraSessionState.Closed;
    }

    private async Task RunAsync(VideoCapture capture, CancellationToken ct)
    {
        using var mat = new Mat();
        var interval = _desiredFps > 0
            ? TimeSpan.FromMilliseconds(1000.0 / _desiredFps)
            : TimeSpan.Zero;
        var consecutiveReadFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            await _pauseGate.WaitIfPausedAsync(ct);
            var readOk = false;
            try
            {
                readOk = capture.Read(mat);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "USB 采集帧失败");
            }
            if (!readOk || mat.Empty())
            {
                if (++consecutiveReadFailures >= 50)
                {
                    Fault(CameraErrorCodes.DeviceDisconnected,
                        "USB 相机连续取帧失败，可能已断开或被其他程序占用", recoverable: true);
                    _capture = null;
                    capture.Dispose();
                    return;
                }
                // DSHOW 偶发空帧，短暂退避后重试
                try
                {
                    await Task.Delay(10, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                continue;
            }
            consecutiveReadFailures = 0;
            var frame = ToFrame(mat, Interlocked.Increment(ref _sequence));
            if (frame is not null)
            {
                FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(frame));
            }
            if (interval > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(interval, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
        if (State == CameraSessionState.Streaming)
        {
            State = CameraSessionState.Idle;
        }
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static VideoFrame? ToFrame(Mat mat, long sequence)
    {
        if (mat.Empty())
        {
            return null;
        }
        var created = false;
        Mat source;
        if (mat.Type() == MatType.CV_8UC3 && mat.IsContinuous())
        {
            source = mat;
        }
        else
        {
            source = ConvertToBgr24(mat);
            created = true;
        }
        try
        {
            var bytes = new byte[source.Width * source.Height * 3];
            Marshal.Copy(source.Data, bytes, 0, bytes.Length);
            return new VideoFrame(sequence, DateTimeOffset.Now, source.Width, source.Height, bytes);
        }
        finally
        {
            if (created)
            {
                source.Dispose();
            }
        }
    }

    private static Mat ConvertToBgr24(Mat mat)
    {
        Mat target;
        if (mat.Channels() == 1)
        {
            target = new Mat();
            Cv2.CvtColor(mat, target, ColorConversionCodes.GRAY2BGR);
        }
        else if (mat.Channels() == 4)
        {
            target = new Mat();
            Cv2.CvtColor(mat, target, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            target = mat.Clone();
        }
        if (target.Type() != MatType.CV_8UC3 || !target.IsContinuous())
        {
            var fixedMat = target.Clone();
            target.Dispose();
            return fixedMat;
        }
        return target;
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
