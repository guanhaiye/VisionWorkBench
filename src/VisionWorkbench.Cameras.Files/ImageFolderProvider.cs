using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Cameras.Files;

/// <summary>图片目录虚拟相机（CAM-005/CAM-010）：按名排序逐帧推送，坏图记录后跳过。</summary>
public sealed class ImageFolderProvider(ILogger? logger = null) : ICameraProvider
{
    public const string ProviderIdValue = "image-folder";

    public string ProviderId => ProviderIdValue;
    public string DisplayName => "图片目录";

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);

    public Task<ICameraSession> CreateSessionAsync(
        CameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        var session = new ImageFolderSession(descriptor, logger);
        return Task.FromResult<ICameraSession>(session);
    }
}

internal sealed class ImageFolderSession(CameraDescriptor descriptor, ILogger? logger) : IImageFolderCameraSession
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];
    private readonly CancellationTokenSource _cts = new();
    private readonly AsyncPauseGate _pauseGate = new();
    private Task? _loopTask;
    private long _sequence;
    private int _badFiles;
    private IReadOnlyList<string> _fileSnapshot = [];
    private int _intervalMs;
    private bool _loop;
    private int? _maxFrames;
    private int _startFrameIndex;
    private int _disposed;

    public CameraDescriptor Descriptor { get; } = descriptor;
    public CameraSessionState State { get; private set; } = CameraSessionState.Idle;
    public CameraCapabilities Capabilities { get; } = new() { SupportsExposureControl = false };
    public int SkippedFileCount => Volatile.Read(ref _badFiles);
    public IReadOnlyList<string> FileSnapshot => _fileSnapshot;

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<CameraFaultedEventArgs>? Faulted;
    public event EventHandler? Completed;

    public Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var dir = Descriptor.DeviceId;
        if (!Directory.Exists(dir))
        {
            State = CameraSessionState.Faulted;
            Faulted?.Invoke(this, new CameraFaultedEventArgs(new CameraFault
            {
                Code = CameraErrorCodes.DeviceNotFound,
                Message = $"图片目录不存在: {dir}",
                Recoverable = false,
            }));
            return Task.CompletedTask;
        }
        _fileSnapshot = options.ImageFiles?.ToArray() ?? EnumerateImages(dir).ToArray();
        _intervalMs = options.FrameIntervalMs;
        _loop = options.Loop;
        _maxFrames = options.MaxFrames;
        _startFrameIndex = Math.Max(0, options.StartFrameIndex);
        State = CameraSessionState.Idle;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (State == CameraSessionState.Streaming || _loopTask is not null)
        {
            return Task.CompletedTask;
        }
        State = CameraSessionState.Streaming;
        _loopTask = Task.Run(() => RunAsync(
            _fileSnapshot,
            TimeSpan.FromMilliseconds(Math.Max(0, _intervalMs)),
            _loop,
            _cts.Token));
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
        _cts.Dispose();
        _pauseGate.Dispose();
        State = CameraSessionState.Closed;
    }

    private async Task RunAsync(IReadOnlyList<string> files, TimeSpan interval, bool loop, CancellationToken ct)
    {
        try
        {
            await PlayAsync(files, interval, loop, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停止或释放时退出暂停等待。
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "图片目录播放失败: {Directory}", Descriptor.DeviceId);
            State = CameraSessionState.Faulted;
            Faulted?.Invoke(this, new CameraFaultedEventArgs(new CameraFault
            {
                Code = CameraErrorCodes.OpenFailed,
                Message = $"图片目录播放失败: {ex.Message}",
                Recoverable = true,
            }));
        }
        finally
        {
            if (State is CameraSessionState.Streaming or CameraSessionState.Paused)
            {
                State = CameraSessionState.Idle;
            }
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PlayAsync(IReadOnlyList<string> files, TimeSpan interval, bool loop, CancellationToken ct)
    {
        var emittedFrames = 0;
        var startFrameIndex = _startFrameIndex;
        while (!ct.IsCancellationRequested)
        {
            var framesBeforePass = emittedFrames;
            foreach (var file in files.Skip(startFrameIndex))
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                await _pauseGate.WaitIfPausedAsync(ct);
                Mat? mat;
                try
                {
                    mat = FrameConvert.DecodeFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
                {
                    Interlocked.Increment(ref _badFiles);
                    logger?.LogWarning(ex, "跳过无法读取的图片（CAM-010）: {File}", file);
                    continue;
                }
                if (mat is null || mat.Empty())
                {
                    Interlocked.Increment(ref _badFiles);
                    logger?.LogWarning("跳过无法解码的图片（CAM-010）: {File}", file);
                    mat?.Dispose();
                    continue;
                }
                using (mat)
                {
                    var frame = FrameConvert.ToFrame(mat, Interlocked.Increment(ref _sequence));
                    if (frame is not null)
                    {
                        FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(frame));
                        emittedFrames++;
                    }
                }
                if (_maxFrames is int currentMaxFrames && emittedFrames >= currentMaxFrames)
                {
                    break;
                }
                if (interval > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(interval, ct);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                }
            }
            if (!loop || (_maxFrames is int reachedMaxFrames && emittedFrames >= reachedMaxFrames))
            {
                break;
            }
            startFrameIndex = 0;
            if (emittedFrames == framesBeforePass)
            {
                // 空目录或全部坏图时也应退避，避免循环模式持续占满 CPU。
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
        }
    }

    internal static IEnumerable<string> EnumerateImages(string dir) =>
        Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.Ordinal);
}
