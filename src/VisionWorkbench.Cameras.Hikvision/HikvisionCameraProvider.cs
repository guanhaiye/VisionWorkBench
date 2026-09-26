using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Cameras.Hikvision;

/// <summary>
/// 海康机器人 MVS 工业相机 Provider：GigE Vision 与 USB3 Vision。
/// 使用 MVS 原生 API 发现设备、打开独占会话、设置 GenICam 参数并取 BGR 帧。
/// </summary>
public sealed class HikvisionCameraProvider(ILogger? logger = null) : ICameraProvider
{
    public const string ProviderIdValue = "hikvision";

    public string ProviderId => ProviderIdValue;
    public string DisplayName => "海康工业相机";

    public Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MvsNative.TryEnsureLoaded())
        {
            logger?.LogInformation("未发现海康 MVS SDK；请安装 MVS Runtime 后再扫描工业相机");
            return Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);
        }

        var list = MvsNative.DeviceList.Create();
        var code = MvsNative.MV_CC_EnumDevices(MvsNative.GigeDevice | MvsNative.UsbDevice, ref list);
        if (code != MvsNative.Ok)
        {
            logger?.LogWarning("海康 MVS 枚举设备失败: {Error}", MvsNative.Error(code));
            return Task.FromResult<IReadOnlyList<CameraDescriptor>>([]);
        }

        var found = new List<CameraDescriptor>();
        var count = (int)Math.Min(list.Count, 256u);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deviceInfo = list.Devices?[index] ?? IntPtr.Zero;
            if (deviceInfo == IntPtr.Zero) continue;
            var layerType = MvsNative.ReadLayerType(deviceInfo);
            if (layerType is not (MvsNative.GigeDevice or MvsNative.UsbDevice)) continue;

            var serial = MvsNative.ReadDeviceSerial(deviceInfo, layerType);
            var model = MvsNative.ReadDeviceModel(deviceInfo, layerType);
            var userName = MvsNative.ReadDeviceUserName(deviceInfo, layerType);
            var id = BuildDeviceId(layerType, serial, deviceInfo, index);
            var endpoint = layerType == MvsNative.GigeDevice
                ? $"，IP {MvsNative.ReadDeviceIp(deviceInfo)}"
                : "，USB3";
            var displayName = string.IsNullOrWhiteSpace(userName) ? model : userName;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"设备 {index + 1}";
            displayName = $"海康 {displayName}{endpoint}{(string.IsNullOrWhiteSpace(serial) ? "" : $"，SN {serial}")}";

            found.Add(new CameraDescriptor
            {
                ProviderId = ProviderIdValue,
                DeviceId = id,
                DisplayName = displayName,
                Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
            });
        }
        logger?.LogInformation("海康 MVS 发现 {Count} 台工业相机", found.Count);
        return Task.FromResult<IReadOnlyList<CameraDescriptor>>(found);
    }

    public Task<ICameraSession> CreateSessionAsync(CameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!string.Equals(descriptor.ProviderId, ProviderIdValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"不是海康工业相机描述: {descriptor.ProviderId}", nameof(descriptor));
        }
        return Task.FromResult<ICameraSession>(new HikvisionCameraSession(descriptor, logger));
    }

    private static string BuildDeviceId(uint layerType, string serial, IntPtr deviceInfo, int index)
    {
        if (!string.IsNullOrWhiteSpace(serial)) return $"{(layerType == MvsNative.GigeDevice ? "gige" : "usb3")}:{serial}";
        return layerType == MvsNative.GigeDevice
            ? $"gige-ip:{MvsNative.ReadDeviceIp(deviceInfo)}"
            : $"usb3-index:{index}";
    }
}

internal sealed class HikvisionCameraSession(CameraDescriptor descriptor, ILogger? logger) : ICameraSession, ITriggerableCameraSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly AsyncPauseGate _pauseGate = new();
    private readonly object _nativeGate = new();
    private readonly object _lifecycleGate = new();
    private Task? _disposeTask;
    private int _disposeRequested;
    private Task? _loopTask;
    private IntPtr _handle;
    private IntPtr _buffer;
    private int _bufferSize;
    private long _sequence;
    private CameraOpenOptions _options = new();
    private bool _deviceOpened;
    private int _faultRaised;

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
            if (_handle != IntPtr.Zero || _loopTask is not null)
            {
                throw new InvalidOperationException("相机会话已打开，请创建新会话重新打开设备");
            }
            cancellationToken.ThrowIfCancellationRequested();
            _options = options;
            if (!MvsNative.TryEnsureLoaded())
            {
                Fault(CameraErrorCodes.SdkNotInstalled, "未安装海康 MVS Runtime，无法打开工业相机", recoverable: false);
                return Task.CompletedTask;
            }

            try
            {
                OpenNative(options);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "打开海康工业相机失败: {DeviceId}", Descriptor.DeviceId);
                ReleaseNative();
                Fault(MapException(ex), $"海康工业相机打开失败：{ex.Message}", recoverable: true);
            }
            return Task.CompletedTask;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (State == CameraSessionState.Streaming || _loopTask is not null) return Task.CompletedTask;
            if (_handle == IntPtr.Zero || !_deviceOpened)
            {
                Fault(CameraErrorCodes.OpenFailed, "海康工业相机会话未打开", recoverable: false);
                return Task.CompletedTask;
            }
            var code = MvsNative.MV_CC_StartGrabbing(_handle);
            if (code != MvsNative.Ok)
            {
                Fault(CameraErrorCodes.StreamStartFailed, $"海康相机开始取流失败：{MvsNative.Error(code)}", recoverable: true);
                return Task.CompletedTask;
            }
            State = CameraSessionState.Streaming;
            _loopTask = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
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
            lock (_nativeGate)
            {
                if (_handle != IntPtr.Zero && _deviceOpened)
                {
                    _ = MvsNative.MV_CC_StopGrabbing(_handle);
                }
            }
            if (State is CameraSessionState.Streaming or CameraSessionState.Paused) State = CameraSessionState.Idle;
            return Task.CompletedTask;
        }
    }

    public Task ApplyParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken)
    {
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            ArgumentNullException.ThrowIfNull(parameters);
            cancellationToken.ThrowIfCancellationRequested();
            if (_handle == IntPtr.Zero) return Task.CompletedTask;
            ApplyParameters(parameters, throwOnError: true);
            return Task.CompletedTask;
        }
    }

    /// <summary>软件触发一次；仅在 TriggerMode=true 且 TriggerSource=Software 时有效。</summary>
    public Task TriggerSoftwareAsync(CancellationToken cancellationToken = default)
    {
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("海康相机尚未打开");
            var code = MvsNative.MV_CC_TriggerSoftwareExecute(_handle);
            if (code != MvsNative.Ok) throw new InvalidOperationException($"海康软件触发失败：{MvsNative.Error(code)}");
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        _cts.Cancel();
        State = CameraSessionState.Closed;
        var stopTask = Task.Run(() =>
        {
            lock (_nativeGate)
            {
                if (_handle != IntPtr.Zero && _deviceOpened) _ = MvsNative.MV_CC_StopGrabbing(_handle);
            }
        });
        var released = await CameraSessionCleanup.ReleaseAfterLoopAsync(
            Task.WhenAll(_loopTask ?? Task.CompletedTask, stopTask), () =>
            {
                try { ReleaseNative(); }
                finally
                {
                    _pauseGate.Dispose();
                    _cts.Dispose();
                }
            }, TimeSpan.FromSeconds(5), ex => logger?.LogWarning(ex, "关闭海康相机会话时清理资源失败"));
        if (!released) logger?.LogWarning("海康采集线程仍未退出，句柄和图像缓冲将在采集结束后释放: {Device}", Descriptor.DeviceId);
    }

    private void OpenNative(CameraOpenOptions options)
    {
        lock (_nativeGate)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var list = MvsNative.DeviceList.Create();
            var code = MvsNative.MV_CC_EnumDevices(MvsNative.GigeDevice | MvsNative.UsbDevice, ref list);
            EnsureOk(code, "枚举海康设备");
            var (device, layerType) = FindDevice(list);
            if (device == IntPtr.Zero) throw new InvalidOperationException("设备已断开或未找到");

            code = MvsNative.MV_CC_CreateHandle(out _handle, device);
            EnsureOk(code, "创建海康相机句柄");
            code = MvsNative.MV_CC_OpenDevice(_handle, MvsNative.AccessExclusive, 0);
            EnsureOk(code, "打开海康相机");
            _deviceOpened = true;
            _ = layerType;

            code = MvsNative.MV_CC_SetImageNodeNum(_handle, 5);
            if (code != MvsNative.Ok) logger?.LogDebug("海康设置缓存节点失败: {Error}", MvsNative.Error(code));

            // 连续采集是实时检测默认模式；触发模式通过 CameraParameterSet 显式开启。
            SetEnumIfSupported("AcquisitionMode", MvsNative.AcquisitionContinuous);
            var parameters = options.Parameters ?? new CameraParameterSet
            {
                Width = options.DesiredWidth,
                Height = options.DesiredHeight,
                FrameRate = options.DesiredFps,
            };
            ApplyParameters(parameters, throwOnError: false);
            if (parameters.TriggerMode is null) SetEnumIfSupported("TriggerMode", MvsNative.TriggerOff);

            var width = ReadInt("Width", 0);
            var height = ReadInt("Height", 0);
            var payload = ReadInt("PayloadSize", 0);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("无法读取海康相机图像尺寸");
            _bufferSize = checked(Math.Max(width * height * 3, Math.Max(payload * 3, 4 * 1024 * 1024)));
            _buffer = Marshal.AllocHGlobal(_bufferSize);
            Capabilities = new CameraCapabilities
            {
                SupportedModes = [new CameraMode { Width = width, Height = height, Fps = ReadFloat("AcquisitionFrameRate", options.DesiredFps ?? 0) }],
                SupportsExposureControl = true,
                SupportsGainControl = true,
                SupportsTrigger = true,
            };
            State = CameraSessionState.Idle;
        }
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _pauseGate.WaitIfPausedAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            if (cancellationToken.IsCancellationRequested || _handle == IntPtr.Zero || !_deviceOpened) break;

            var info = new MvsNative.FrameInfoEx();
            var code = MvsNative.MV_CC_GetImageForBGR(_handle, _buffer, checked((uint)_bufferSize), ref info, 1000);
            if (cancellationToken.IsCancellationRequested) break;
            if (code != MvsNative.Ok)
            {
                failures++;
                if (failures >= 5 && !await TryReconnectAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    Fault(CameraErrorCodes.DeviceDisconnected,
                        $"海康相机取流失败，已尝试自动重连：{MvsNative.Error(code)}", recoverable: true);
                    break;
                }
                continue;
            }
            failures = 0;
            var width = info.Width;
            var height = info.Height;
            var length = checked(width * height * 3);
            if (width == 0 || height == 0 || length > _bufferSize) continue;
            var pixels = new byte[length];
            Marshal.Copy(_buffer, pixels, 0, length);
            FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(
                new VideoFrame(Interlocked.Increment(ref _sequence), DateTimeOffset.Now, width, height, pixels)));
        }
        if (State is CameraSessionState.Streaming or CameraSessionState.Paused) State = CameraSessionState.Idle;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        logger?.LogWarning("海康相机取流异常，准备自动重连: {DeviceId}", Descriptor.DeviceId);
        ReleaseNative();
        for (var attempt = 1; attempt <= 10 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, attempt * 200)), cancellationToken);
                lock (_nativeGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OpenNative(_options);
                    cancellationToken.ThrowIfCancellationRequested();
                    var code = MvsNative.MV_CC_StartGrabbing(_handle);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (code == MvsNative.Ok)
                    {
                        State = CameraSessionState.Streaming;
                        logger?.LogInformation("海康相机自动重连成功，第 {Attempt} 次尝试", attempt);
                        return true;
                    }
                    ReleaseNative();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReleaseNative();
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "海康相机重连失败，第 {Attempt} 次尝试", attempt);
                ReleaseNative();
            }
        }
        return false;
    }

    private (IntPtr Pointer, uint LayerType) FindDevice(MvsNative.DeviceList list)
    {
        var count = (int)Math.Min(list.Count, 256u);
        for (var index = 0; index < count; index++)
        {
            var pointer = list.Devices?[index] ?? IntPtr.Zero;
            if (pointer == IntPtr.Zero) continue;
            var layerType = MvsNative.ReadLayerType(pointer);
            var serial = MvsNative.ReadDeviceSerial(pointer, layerType);
            var candidate = BuildId(layerType, serial, pointer, index);
            if (string.Equals(candidate, Descriptor.DeviceId, StringComparison.OrdinalIgnoreCase)) return (pointer, layerType);
        }
        return (IntPtr.Zero, 0);
    }

    private static string BuildId(uint layerType, string serial, IntPtr pointer, int index)
    {
        if (!string.IsNullOrWhiteSpace(serial)) return $"{(layerType == MvsNative.GigeDevice ? "gige" : "usb3")}:{serial}";
        return layerType == MvsNative.GigeDevice ? $"gige-ip:{MvsNative.ReadDeviceIp(pointer)}" : $"usb3-index:{index}";
    }

    private void ApplyParameters(CameraParameterSet parameters, bool throwOnError)
    {
        SetIntIfRequested("Width", parameters.Width, throwOnError);
        SetIntIfRequested("Height", parameters.Height, throwOnError);
        SetFloatIfRequested("AcquisitionFrameRate", parameters.FrameRate, throwOnError);
        if (parameters.AutoExposure is { } autoExposure)
        {
            SetEnum("ExposureAuto", autoExposure ? 2u : 0u, throwOnError);
        }
        SetFloatIfRequested("ExposureTime", parameters.ExposureTimeUs, throwOnError);
        if (parameters.AutoGain is { } autoGain)
        {
            SetEnum("GainAuto", autoGain ? 2u : 0u, throwOnError);
        }
        SetFloatIfRequested("Gain", parameters.GainDb, throwOnError);
        if (parameters.PixelFormat is { } pixelFormat) SetEnum("PixelFormat", pixelFormat, throwOnError);
        if (parameters.TriggerMode is { } triggerMode) SetEnum("TriggerMode", triggerMode ? MvsNative.TriggerOn : MvsNative.TriggerOff, throwOnError);
        if (!string.IsNullOrWhiteSpace(parameters.TriggerSource))
        {
            var source = parameters.TriggerSource.Equals("Software", StringComparison.OrdinalIgnoreCase)
                ? MvsNative.TriggerSourceSoftware
                : parameters.TriggerSource.Equals("Line0", StringComparison.OrdinalIgnoreCase)
                    ? MvsNative.TriggerSourceLine0
                    : uint.TryParse(parameters.TriggerSource, out var value) ? value : uint.MaxValue;
            if (source != uint.MaxValue) SetEnum("TriggerSource", source, throwOnError);
        }
    }

    private int ReadInt(string key, int fallback)
    {
        var value = new MvsNative.IntValue();
        return MvsNative.MV_CC_GetIntValue(_handle, key, ref value) == MvsNative.Ok ? checked((int)value.Current) : fallback;
    }

    private double ReadFloat(string key, double fallback)
    {
        var value = new MvsNative.FloatValue();
        return MvsNative.MV_CC_GetFloatValue(_handle, key, ref value) == MvsNative.Ok ? value.Current : fallback;
    }

    private void SetIntIfRequested(string key, int? value, bool throwOnError)
    {
        if (value is not { } actual || actual <= 0) return;
        Check(MvsNative.MV_CC_SetIntValue(_handle, key, (uint)actual), key, throwOnError);
    }

    private void SetFloatIfRequested(string key, double? value, bool throwOnError)
    {
        if (value is not { } actual || actual <= 0) return;
        Check(MvsNative.MV_CC_SetFloatValue(_handle, key, (float)actual), key, throwOnError);
    }

    private void SetEnum(string key, uint value, bool throwOnError) => Check(MvsNative.MV_CC_SetEnumValue(_handle, key, value), key, throwOnError);
    private void SetEnumIfSupported(string key, uint value) => Check(MvsNative.MV_CC_SetEnumValue(_handle, key, value), key, false);

    private void Check(int code, string key, bool throwOnError)
    {
        if (code == MvsNative.Ok) return;
        if (throwOnError) throw new InvalidOperationException($"设置海康参数 {key} 失败：{MvsNative.Error(code)}");
        logger?.LogDebug("海康参数 {Key} 不支持或设置失败: {Error}", key, MvsNative.Error(code));
    }

    private static void EnsureOk(int code, string operation)
    {
        if (code != MvsNative.Ok) throw new InvalidOperationException($"{operation}失败：{MvsNative.Error(code)}");
    }

    private static string MapException(Exception ex) => ex.Message.Contains("MVS", StringComparison.OrdinalIgnoreCase)
        ? CameraErrorCodes.SdkVersionMismatch
        : CameraErrorCodes.OpenFailed;

    private void Fault(string code, string message, bool recoverable)
    {
        if (Volatile.Read(ref _disposeRequested) != 0 || _cts.IsCancellationRequested) return;
        State = CameraSessionState.Faulted;
        if (Interlocked.Exchange(ref _faultRaised, 1) == 0)
        {
            Faulted?.Invoke(this, new CameraFaultedEventArgs(new CameraFault { Code = code, Message = message, Recoverable = recoverable }));
        }
    }

    private void ReleaseNative()
    {
        lock (_nativeGate)
        {
            if (_handle != IntPtr.Zero)
            {
                if (_deviceOpened) _ = MvsNative.MV_CC_CloseDevice(_handle);
                _ = MvsNative.MV_CC_DestroyHandle(_handle);
            }
            _handle = IntPtr.Zero;
            _deviceOpened = false;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }
}
