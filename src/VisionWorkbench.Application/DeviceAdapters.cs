namespace VisionWorkbench.Application;

using System.Net.Sockets;

public enum DeviceCapability { Camera, Trigger, Plc, Serial, Heartbeat }
public sealed record DeviceHealth(string DeviceId, bool IsHealthy, string Message, DateTime CheckedAtUtc);

/// <summary>工业设备统一适配器契约；厂商 SDK 只能通过该边界进入业务层。</summary>
public interface IIndustrialDeviceAdapter : IAsyncDisposable
{
    string DeviceId { get; }
    IReadOnlySet<DeviceCapability> Capabilities { get; }
    Task<DeviceHealth> CheckHealthAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}

public sealed class UnsupportedDeviceAdapter(string deviceId, IReadOnlySet<DeviceCapability> capabilities) : IIndustrialDeviceAdapter
{
    public string DeviceId { get; } = deviceId;
    public IReadOnlySet<DeviceCapability> Capabilities { get; } = capabilities;
    public Task<DeviceHealth> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(new DeviceHealth(DeviceId, false, "设备适配器未安装或未通过兼容性认证", DateTime.UtcNow));
    public Task StartAsync(CancellationToken ct = default) => throw new NotSupportedException("设备适配器未安装");
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) => throw new NotSupportedException("设备适配器未安装");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record IndustrialDeviceDescriptor(
    string DeviceId,
    string Vendor,
    string Model,
    string SerialNumber,
    string FirmwareVersion,
    string InterfaceType,
    string? Address = null,
    string? MacAddress = null);

/// <summary>Thread-safe adapter registry. Vendor SDKs can be plugged in without leaking into business services.</summary>
public sealed class DeviceAdapterRegistry
{
    private readonly Dictionary<string, IIndustrialDeviceAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(IIndustrialDeviceAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        lock (_gate) _adapters[adapter.DeviceId] = adapter;
    }

    public bool TryGet(string deviceId, out IIndustrialDeviceAdapter? adapter)
    {
        lock (_gate) return _adapters.TryGetValue(deviceId, out adapter);
    }

    public IReadOnlyList<string> DeviceIds { get { lock (_gate) return _adapters.Keys.OrderBy(x => x).ToArray(); } }
}

/// <summary>Minimal Modbus TCP transport. It only sends caller-built frames; safety interlocks belong above this boundary.</summary>
public sealed class ModbusTcpAdapter(string deviceId, string host, int port = 502) : IIndustrialDeviceAdapter
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    public string DeviceId { get; } = deviceId;
    public IReadOnlySet<DeviceCapability> Capabilities { get; } = new HashSet<DeviceCapability> { DeviceCapability.Plc, DeviceCapability.Heartbeat };

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_client is { Connected: true }) return;
        _client?.Dispose();
        _client = new TcpClient { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
        await _client.ConnectAsync(host, port, ct);
        _stream = _client.GetStream();
    }

    public async Task<DeviceHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await StartAsync(ct);
            return new DeviceHealth(DeviceId, _client?.Connected == true, "Modbus TCP connected", DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or OperationCanceledException)
        {
            return new DeviceHealth(DeviceId, false, "Modbus TCP unavailable: " + ex.Message, DateTime.UtcNow);
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length is < 8 or > 260) throw new ArgumentOutOfRangeException(nameof(payload), "Modbus TCP frame length is invalid");
        if (_stream is null) await StartAsync(ct);
        await _stream!.WriteAsync(payload, ct);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _stream?.Dispose(); _stream = null; _client?.Dispose(); _client = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { _ = StopAsync(); return ValueTask.CompletedTask; }
}
