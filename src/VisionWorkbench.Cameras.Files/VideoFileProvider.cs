using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Cameras.Files;

/// <summary>视频文件虚拟相机（CAM-008/CAM-009）：可配 FPS/循环；损坏文件返回可理解错误。</summary>
public sealed class VideoFileProvider(ILogger? logger = null) : ICameraProvider
{
    public const string ProviderIdValue = "video-file";

    public string ProviderId => ProviderIdValue;
    public string DisplayName => "视频文件";

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);

    public Task<ICameraSession> CreateSessionAsync(
        CameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        var session = new VideoFileSession(descriptor, logger);
        return Task.FromResult<ICameraSession>(session);
    }
}

internal sealed class VideoFileSession(
    CameraDescriptor descriptor, ILogger? logger) : ICameraSession
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private VideoCapture? _capture;
    private long _sequence;
    private double _fps = 25;
    private bool _loop;
    private int _intervalMsOverride;
    private int? _maxFrames;
    private int _startFrameIndex;
    private string? _asciiFallbackPath;

    public CameraDescriptor Descriptor { get; } = descriptor;
    public CameraSessionState State { get; private set; } = CameraSessionState.Idle;
    public CameraCapabilities Capabilities { get; private set; } = new();

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<CameraFaultedEventArgs>? Faulted;
    public event EventHandler? Completed;

    public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken)
    {
        var path = Descriptor.DeviceId;
        if (!File.Exists(path))
        {
            Fault(CameraErrorCodes.DeviceNotFound, $"视频文件不存在: {path}", recoverable: false);
            return Task.CompletedTask;
        }

        _capture = OpenCapture(path);
        if (_capture is null || !_capture.IsOpened())
        {
            _capture?.Dispose();
            _capture = null;
            Fault(CameraErrorCodes.OpenFailed,
                $"视频文件无法打开（可能已损坏或格式不受支持，CAM-009）: {path}", recoverable: false);
            return Task.CompletedTask;
        }

        _fps = options.DesiredFps ?? (_capture.Fps > 0 ? _capture.Fps : 25);
        _intervalMsOverride = options.FrameIntervalMs;
        _loop = options.Loop;
        _maxFrames = options.MaxFrames;
        _startFrameIndex = Math.Max(0, options.StartFrameIndex);
        if (_startFrameIndex > 0)
        {
            _capture.Set(VideoCaptureProperties.PosFrames, _startFrameIndex);
        }
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

    /// <summary>OpenCV 后端对非 ASCII 路径支持不稳，失败时复制到临时 ASCII 路径再打开。</summary>
    private VideoCapture? OpenCapture(string path)
    {
        var capture = new VideoCapture(path);
        if (capture.IsOpened())
        {
            return capture;
        }
        capture.Dispose();
        if (IsAscii(path))
        {
            return null;
        }
        try
        {
            _asciiFallbackPath = Path.Combine(
                Path.GetTempPath(), $"vw-video-{Guid.NewGuid():N}{Path.GetExtension(path)}");
            File.Copy(path, _asciiFallbackPath, overwrite: true);
            var fallback = new VideoCapture(_asciiFallbackPath);
            if (fallback.IsOpened())
            {
                logger?.LogDebug("视频通过临时 ASCII 路径打开: {Temp}", _asciiFallbackPath);
                return fallback;
            }
            fallback.Dispose();
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "复制视频到临时路径失败");
        }
        return null;
    }

    private static bool IsAscii(string path) => path.All(c => c < 128);

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
                // 播放线程自行退出
            }
        }
        _capture?.Dispose();
        _capture = null;
        if (_asciiFallbackPath is not null)
        {
            try
            {
                File.Delete(_asciiFallbackPath);
            }
            catch (IOException)
            {
                // 临时文件残留由系统清理
            }
        }
        State = CameraSessionState.Closed;
    }

    private async Task RunAsync(VideoCapture capture, CancellationToken ct)
    {
        using var mat = new Mat();
        var emittedFrames = 0;
        var interval = _intervalMsOverride > 0
            ? TimeSpan.FromMilliseconds(_intervalMsOverride)
            : TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _fps));

        while (!ct.IsCancellationRequested)
        {
            var readOk = false;
            try
            {
                readOk = capture.Read(mat);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "读视频帧失败");
            }
            if (!readOk || mat.Empty())
            {
                if (_loop)
                {
                    capture.Set(VideoCaptureProperties.PosFrames, 0);
                    await DelaySafe(TimeSpan.FromMilliseconds(200), ct);
                    continue;
                }
                break;
            }
            var frame = FrameConvert.ToFrame(mat, Interlocked.Increment(ref _sequence));
            if (frame is not null)
            {
                FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(frame));
                emittedFrames++;
            }
            if (_maxFrames is int maxFrames && emittedFrames >= maxFrames)
            {
                break;
            }
            await DelaySafe(interval, ct);
        }
        if (State == CameraSessionState.Streaming)
        {
            State = CameraSessionState.Idle;
        }
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (TaskCanceledException)
        {
            // 由调用方检查 ct
        }
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
