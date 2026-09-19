using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace VisionWorkbench.Application.Communication;

public enum TcpWorkMode { Disabled, Server, Client }
public enum TcpFrameMode { Line, Delimiter, LengthPrefix }
public enum TcpRuntimeState { Stopped, Starting, Listening, Connecting, Connected, Reconnecting, Stopping, Faulted }
public enum MessageMatchMode { JsonField, ExactText, Prefix, Regex, Default }
public enum BusyPolicy { RejectWhenBusy, Queue, ReplacePending }
public enum OfflineResponsePolicy { Drop, KeepLatest, StoreAndRetry }

public sealed class TaskTcpTriggerConfig
{
    public bool Enabled { get; set; }
    public string? RuleName { get; set; }
    /// <summary>TCP/IP 项目（配置档案）编码。为空时兼容旧任务并使用默认项目。</summary>
    public string? TcpProjectCode { get; set; }
    public MessageMatchMode MatchMode { get; set; } = MessageMatchMode.ExactText;
    public string MatchValue { get; set; } = "";
    public string ResponseTemplate { get; set; } = "{\"ok\":true,\"code\":\"completed\",\"requestId\":\"{requestId}\",\"task\":\"{task}\",\"status\":\"{status}\",\"count\":{count}}";
}

public sealed record TcpTaskExecutionResult(string Status, long Count, string Decision, long RecordId = 0);

public sealed class ProjectCommunicationConfig
{
    public string ProjectCode { get; set; } = "default";
    public string Name { get; set; } = "默认 TCP/IP 项目";
    public bool Enabled { get; set; }
    public TcpWorkMode WorkMode { get; set; } = TcpWorkMode.Disabled;
    public string Encoding { get; set; } = "utf-8";
    public TcpFrameMode FrameMode { get; set; } = TcpFrameMode.Line;
    public string MessageTerminator { get; set; } = "\\n";
    public int LengthPrefixBytes { get; set; } = 4;
    public bool LengthPrefixBigEndian { get; set; } = true;
    public int MaxMessageBytes { get; set; } = 1024 * 1024;
    public int ReceiveTimeoutMs { get; set; } = 30000;
    public int SendTimeoutMs { get; set; } = 10000;
    public bool AutoStart { get; set; } = true;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 5000;
    public int MaxConnections { get; set; } = 10;
    public string RemoteAddress { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 5000;
    public string LocalBindAddress { get; set; } = "";
    public int LocalBindPort { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectIntervalMs { get; set; } = 3000;
    public int MaxReconnectIntervalMs { get; set; } = 30000;
    public bool HeartbeatEnabled { get; set; }
    public int HeartbeatIntervalMs { get; set; } = 10000;
    public string HeartbeatMessage { get; set; } = "";
    public bool RequireAuthentication { get; set; }
    public string ClientId { get; set; } = "";
    public string SharedSecret { get; set; } = "";
    public int MaxClockSkewSeconds { get; set; } = 120;
    public int RequestCacheDays { get; set; } = 7;
    public bool TlsEnabled { get; set; }
    public string? ServerCertificatePath { get; set; }
    public string? TrustedServerCertificateSha256 { get; set; }
    public bool AllowUntrustedCertificate { get; set; }
    public List<string> AllowedClientAddresses { get; set; } = [];
    public int MaxRequestsPerMinute { get; set; } = 1200;
    public int IdleTimeoutSeconds { get; set; } = 300;
}

/// <summary>TCP/IP 项目配置文件的统一读写入口，兼容旧版字典格式。</summary>
public sealed class TcpCommunicationProfileStore
{
    private readonly string _filePath;

    public TcpCommunicationProfileStore(string configDirectory)
    {
        _filePath = Path.Combine(configDirectory, "tcp-communication.json");
    }

    public string FilePath => _filePath;

    public List<ProjectCommunicationConfig> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [CreateDefault()];
            }

            var all = JsonSerializer.Deserialize<Dictionary<string, ProjectCommunicationConfig>>(
                File.ReadAllText(_filePath)) ?? [];
            var profiles = all.Values
                .Where(profile => profile is not null)
                .Select(profile => Normalize(profile))
                .GroupBy(profile => profile.ProjectCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(profile => profile.ProjectCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return profiles.Count == 0 ? [CreateDefault()] : profiles;
        }
        catch
        {
            return [CreateDefault()];
        }
    }

    public void Save(IEnumerable<ProjectCommunicationConfig> profiles)
    {
        var normalized = profiles
            .Select(Normalize)
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ProjectCode))
            .GroupBy(profile => profile.ProjectCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(profile => profile.ProjectCode, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static ProjectCommunicationConfig CreateDefault() => new()
    {
        ProjectCode = "default",
        Name = "默认 TCP/IP 项目",
    };

    private static ProjectCommunicationConfig Normalize(ProjectCommunicationConfig profile)
    {
        profile.ProjectCode = string.IsNullOrWhiteSpace(profile.ProjectCode) ? "default" : profile.ProjectCode.Trim();
        profile.Name = string.IsNullOrWhiteSpace(profile.Name)
            ? profile.ProjectCode == "default" ? "默认 TCP/IP 项目" : profile.ProjectCode
            : profile.Name.Trim();
        return profile;
    }
}

public sealed class TcpConnectionInfo
{
    public string ConnectionId { get; init; } = "";
    public string RemoteEndpoint { get; init; } = "";
    public string LocalEndpoint { get; init; } = "";
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}

public sealed class TcpFrameReceivedEventArgs(TcpConnectionInfo connection, byte[] data) : EventArgs
{
    public TcpConnectionInfo Connection { get; } = connection;
    public byte[] Data { get; } = data;
}

public sealed class TcpRawDataReceivedEventArgs(TcpConnectionInfo connection, byte[] data) : EventArgs
{
    public TcpConnectionInfo Connection { get; } = connection;
    public byte[] Data { get; } = data;
}

public sealed class TcpConnectionChangedEventArgs(TcpConnectionInfo connection, bool connected, string? error = null) : EventArgs
{
    public TcpConnectionInfo Connection { get; } = connection;
    public bool Connected { get; } = connected;
    public string? Error { get; } = error;
}

public sealed record TcpLogEntry(DateTime Timestamp, string Level, string Direction, string ConnectionId, string Message);

public interface ITcpTransport : IAsyncDisposable
{
    TcpRuntimeState State { get; }
    IReadOnlyCollection<TcpConnectionInfo> Connections { get; }
    event EventHandler<TcpRawDataReceivedEventArgs>? RawDataReceived;
    event EventHandler<TcpFrameReceivedEventArgs>? FrameReceived;
    event EventHandler<TcpConnectionChangedEventArgs>? ConnectionChanged;
    Task StartAsync(ProjectCommunicationConfig config, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendAsync(string connectionId, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
}

public sealed class TcpFrameDecoder(ProjectCommunicationConfig config)
{
    private readonly List<byte> _buffer = [];
    private readonly byte[] _terminator = DecodeTerminator(config.MessageTerminator);
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data.ToArray());
        if (_buffer.Count > config.MaxMessageBytes + Math.Max(16, _terminator.Length))
            throw new InvalidDataException("TCP 消息超过最大长度限制");
        var frames = new List<byte[]>();
        while (true)
        {
            var length = FindFrameLength();
            if (length is null) break;
            var frameLength = length.Value;
            if (frameLength > config.MaxMessageBytes) throw new InvalidDataException("TCP 消息超过最大长度限制");
            var prefixLength = config.FrameMode == TcpFrameMode.LengthPrefix ? config.LengthPrefixBytes : 0;
            frames.Add(_buffer.Skip(prefixLength).Take(frameLength).ToArray());
            var suffixLength = config.FrameMode == TcpFrameMode.LengthPrefix
                ? config.LengthPrefixBytes
                : config.FrameMode == TcpFrameMode.Line
                    ? 1 + (frameLength < _buffer.Count && _buffer[frameLength] == 0x0D ? 1 : 0)
                    : _terminator.Length;
            _buffer.RemoveRange(0, frameLength + suffixLength);
        }
        return frames;
    }

    private int? FindFrameLength()
    {
        if (config.FrameMode == TcpFrameMode.LengthPrefix)
        {
            if (_buffer.Count < config.LengthPrefixBytes) return null;
            if (config.LengthPrefixBytes is not (2 or 4)) throw new InvalidDataException("长度前缀只能是 2 或 4 字节");
            uint length = config.LengthPrefixBytes == 2
                ? (config.LengthPrefixBigEndian ? (uint)(_buffer[0] << 8 | _buffer[1]) : (uint)(_buffer[1] << 8 | _buffer[0]))
                : config.LengthPrefixBigEndian
                    ? (uint)(_buffer[0] << 24 | _buffer[1] << 16 | _buffer[2] << 8 | _buffer[3])
                    : (uint)(_buffer[3] << 24 | _buffer[2] << 16 | _buffer[1] << 8 | _buffer[0]);
            if (length > config.MaxMessageBytes) throw new InvalidDataException("TCP 消息超过最大长度限制");
            return _buffer.Count >= config.LengthPrefixBytes + length ? (int)length : null;
        }
        if (config.FrameMode == TcpFrameMode.Line) return FindBytes([0x0A], trimCr: true);
        return FindBytes(_terminator, trimCr: false);
    }

    private int? FindBytes(byte[] marker, bool trimCr)
    {
        if (marker.Length == 0) throw new InvalidDataException("消息分隔符不能为空");
        for (var i = 0; i <= _buffer.Count - marker.Length; i++)
        {
            if (!marker.SequenceEqual(_buffer.Skip(i).Take(marker.Length))) continue;
            var length = i;
            if (trimCr && length > 0 && _buffer[length - 1] == 0x0D) length--;
            return length;
        }
        return null;
    }

    internal static byte[] DecodeTerminator(string value)
    {
        value ??= "\\n";
        if (value.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase))
            return Convert.FromHexString(value[4..].Replace(" ", ""));
        return Encoding.UTF8.GetBytes(value.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t"));
    }
}

public static class TcpMessageCodec
{
    public static Encoding GetEncoding(ProjectCommunicationConfig config) =>
        System.Text.Encoding.GetEncoding(config.Encoding switch { "gbk" or "gb2312" => "gb18030", _ => config.Encoding });

    public static byte[] Encode(string text, ProjectCommunicationConfig config)
    {
        var body = GetEncoding(config).GetBytes(text);
        if (config.FrameMode == TcpFrameMode.LengthPrefix)
        {
            if (config.LengthPrefixBytes is not (2 or 4)) throw new InvalidDataException("长度前缀只能是 2 或 4 字节");
            var prefix = new byte[config.LengthPrefixBytes];
            if (config.LengthPrefixBytes == 2)
            {
                if (body.Length > ushort.MaxValue) throw new InvalidDataException("消息太长");
                if (config.LengthPrefixBigEndian) { prefix[0] = (byte)(body.Length >> 8); prefix[1] = (byte)body.Length; }
                else { prefix[1] = (byte)(body.Length >> 8); prefix[0] = (byte)body.Length; }
            }
            else if (config.LengthPrefixBigEndian)
            {
                prefix[0] = (byte)(body.Length >> 24); prefix[1] = (byte)(body.Length >> 16); prefix[2] = (byte)(body.Length >> 8); prefix[3] = (byte)body.Length;
            }
            else
            {
                prefix[3] = (byte)(body.Length >> 24); prefix[2] = (byte)(body.Length >> 16); prefix[1] = (byte)(body.Length >> 8); prefix[0] = (byte)body.Length;
            }
            return prefix.Concat(body).ToArray();
        }
        if (config.FrameMode == TcpFrameMode.Line || config.FrameMode == TcpFrameMode.Delimiter)
            return body.Concat(TcpFrameDecoder.DecodeTerminator(config.MessageTerminator)).ToArray();
        return body;
    }
}

internal sealed class TcpConnectionSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private Stream _stream;
    private readonly TcpFrameDecoder _decoder;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    public TcpConnectionInfo Info { get; }
    public event EventHandler<TcpRawDataReceivedEventArgs>? RawDataReceived;
    public event EventHandler<TcpFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<string?>? Closed;

    public TcpConnectionSession(TcpClient client, ProjectCommunicationConfig config, string id)
    {
        _client = client; _stream = client.GetStream(); _decoder = new TcpFrameDecoder(config);
        Info = new TcpConnectionInfo { ConnectionId = id, RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "", LocalEndpoint = client.Client.LocalEndPoint?.ToString() ?? "" };
    }
    public async Task RunAsync(ProjectCommunicationConfig config, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var buffer = new byte[81920];
        try
        {
            if (config.TlsEnabled)
            {
                var ssl = new SslStream(_stream, leaveInnerStreamOpen: false, (_, certificate, _, errors) =>
                    config.AllowUntrustedCertificate || CertificateMatches(certificate, config.TrustedServerCertificateSha256) || errors == SslPolicyErrors.None);
                if (config.WorkMode == TcpWorkMode.Server)
                {
                    if (string.IsNullOrWhiteSpace(config.ServerCertificatePath) || !File.Exists(config.ServerCertificatePath)) throw new InvalidOperationException("TLS 服务端证书不存在");
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = X509CertificateLoader.LoadCertificateFromFile(config.ServerCertificatePath), EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, linked.Token);
                }
                else
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = config.RemoteAddress, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, RemoteCertificateValidationCallback = (_, certificate, _, errors) => config.AllowUntrustedCertificate || CertificateMatches(certificate, config.TrustedServerCertificateSha256) || errors == SslPolicyErrors.None }, linked.Token);
                }
                _stream = ssl;
            }
            while (!linked.Token.IsCancellationRequested)
            {
                if (config.IdleTimeoutSeconds > 0 && DateTime.UtcNow - Info.LastActivityAt > TimeSpan.FromSeconds(config.IdleTimeoutSeconds)) throw new TimeoutException("TCP 空闲连接超时");
                var read = await _stream.ReadAsync(buffer, linked.Token);
                if (read == 0) break;
                Info.LastActivityAt = DateTime.UtcNow;
                RawDataReceived?.Invoke(this, new TcpRawDataReceivedEventArgs(Info, buffer[..read].ToArray()));
                foreach (var frame in _decoder.Append(buffer.AsSpan(0, read))) FrameReceived?.Invoke(this, new TcpFrameReceivedEventArgs(Info, frame));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Closed?.Invoke(this, ex.Message); return; }
        Closed?.Invoke(this, null);
    }
    private static bool CertificateMatches(X509Certificate? certificate, string? expected)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(expected)) return false;
        using var cert = new X509Certificate2(certificate); return string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), expected.Replace(":", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try { await _stream.WriteAsync(data, ct); await _stream.FlushAsync(ct); Info.LastActivityAt = DateTime.UtcNow; }
        finally { _sendGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _client.Close(); } catch { }
        _sendGate.Dispose(); _stop.Dispose(); _stream.Dispose();
        await Task.CompletedTask;
    }
}

public sealed class TcpServerTransport : ITcpTransport
{
    private readonly ConcurrentDictionary<string, TcpConnectionSession> _sessions = new();
    private CancellationTokenSource? _stop;
    private Task? _acceptTask;
    private ProjectCommunicationConfig _config = new();
    public TcpRuntimeState State { get; private set; } = TcpRuntimeState.Stopped;
    public IReadOnlyCollection<TcpConnectionInfo> Connections => _sessions.Values.Select(s => s.Info).ToArray();
    public event EventHandler<TcpRawDataReceivedEventArgs>? RawDataReceived;
    public event EventHandler<TcpFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<TcpConnectionChangedEventArgs>? ConnectionChanged;
    public async Task StartAsync(ProjectCommunicationConfig config, CancellationToken ct = default)
    {
        await StopAsync(ct); _config = config; _stop = CancellationTokenSource.CreateLinkedTokenSource(ct); State = TcpRuntimeState.Starting;
        var listener = new TcpListener(ParseAddress(config.ListenAddress), config.ListenPort); listener.Start(); State = TcpRuntimeState.Listening;
        _acceptTask = AcceptLoopAsync(listener, _stop.Token);
    }
    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (_sessions.Count >= Math.Max(1, _config.MaxConnections)) { client.Close(); continue; }
                var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                if (_config.AllowedClientAddresses.Count > 0 && !_config.AllowedClientAddresses.Contains(remoteAddress, StringComparer.OrdinalIgnoreCase)) { client.Close(); continue; }
                var id = Guid.NewGuid().ToString("N")[..12];
                var session = new TcpConnectionSession(client, _config, id);
                if (!_sessions.TryAdd(id, session)) { await session.DisposeAsync(); continue; }
                session.RawDataReceived += (_, e) => RawDataReceived?.Invoke(this, e);
                session.FrameReceived += (_, e) => FrameReceived?.Invoke(this, e);
                session.Closed += async (_, error) => await RemoveAsync(id, session, error);
                ConnectionChanged?.Invoke(this, new TcpConnectionChangedEventArgs(session.Info, true));
                _ = session.RunAsync(_config, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { State = TcpRuntimeState.Faulted; }
        finally { listener.Stop(); }
    }
    private async Task RemoveAsync(string id, TcpConnectionSession session, string? error)
    {
        if (_sessions.TryRemove(id, out _))
        {
            ConnectionChanged?.Invoke(this, new TcpConnectionChangedEventArgs(session.Info, false, error));
            await session.DisposeAsync();
        }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_stop is null) { State = TcpRuntimeState.Stopped; return; }
        State = TcpRuntimeState.Stopping; _stop.Cancel();
        if (_acceptTask is not null) try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
        foreach (var pair in _sessions.ToArray()) await RemoveAsync(pair.Key, pair.Value, "server_stopped");
        _stop.Dispose(); _stop = null; _acceptTask = null; State = TcpRuntimeState.Stopped;
    }
    public async Task SendAsync(string connectionId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(connectionId, out var session)) throw new InvalidOperationException("连接不存在");
        await session.SendAsync(data, ct);
    }
    public async Task BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    { foreach (var session in _sessions.Values) try { await session.SendAsync(data, ct); } catch { } }
    public async ValueTask DisposeAsync() => await StopAsync();
    private static IPAddress ParseAddress(string value) => string.IsNullOrWhiteSpace(value) || value == "0.0.0.0" ? IPAddress.Any : IPAddress.TryParse(value, out var ip) ? ip : Dns.GetHostAddresses(value).First();
}

public sealed class TcpClientTransport : ITcpTransport
{
    private readonly ConcurrentDictionary<string, TcpConnectionSession> _sessions = new();
    private CancellationTokenSource? _stop;
    private Task? _connectTask;
    private ProjectCommunicationConfig _config = new();
    public TcpRuntimeState State { get; private set; } = TcpRuntimeState.Stopped;
    public IReadOnlyCollection<TcpConnectionInfo> Connections => _sessions.Values.Select(s => s.Info).ToArray();
    public event EventHandler<TcpRawDataReceivedEventArgs>? RawDataReceived;
    public event EventHandler<TcpFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<TcpConnectionChangedEventArgs>? ConnectionChanged;
    public async Task StartAsync(ProjectCommunicationConfig config, CancellationToken ct = default)
    {
        await StopAsync(ct); _config = config; _stop = CancellationTokenSource.CreateLinkedTokenSource(ct); State = TcpRuntimeState.Connecting; _connectTask = ConnectLoopAsync(_stop.Token);
    }
    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = Math.Max(200, _config.ReconnectIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                if (!string.IsNullOrWhiteSpace(_config.LocalBindAddress)) client.Client.Bind(new IPEndPoint(IPAddress.Parse(_config.LocalBindAddress), _config.LocalBindPort));
                await client.ConnectAsync(_config.RemoteAddress, _config.RemotePort, ct);
                var id = "client";
                var session = new TcpConnectionSession(client, _config, id); _sessions[id] = session; State = TcpRuntimeState.Connected;
                session.RawDataReceived += (_, e) => RawDataReceived?.Invoke(this, e);
                session.FrameReceived += (_, e) => FrameReceived?.Invoke(this, e);
                session.Closed += async (sender, error) => { _sessions.TryRemove(id, out var removed); ConnectionChanged?.Invoke(this, new TcpConnectionChangedEventArgs(session.Info, false, error)); await session.DisposeAsync(); };
                ConnectionChanged?.Invoke(this, new TcpConnectionChangedEventArgs(session.Info, true));
                await session.RunAsync(_config, ct);
                State = ct.IsCancellationRequested ? TcpRuntimeState.Stopped : TcpRuntimeState.Reconnecting;
                delay = Math.Max(200, _config.ReconnectIntervalMs);
            }
            catch (OperationCanceledException) { break; }
            catch { State = TcpRuntimeState.Reconnecting; }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = Math.Min(Math.Max(delay * 2, _config.ReconnectIntervalMs), Math.Max(delay, _config.MaxReconnectIntervalMs));
            if (!_config.AutoReconnect) break;
        }
        State = TcpRuntimeState.Stopped;
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_stop is null) { State = TcpRuntimeState.Stopped; return; }
        State = TcpRuntimeState.Stopping; _stop.Cancel();
        if (_connectTask is not null) try { await _connectTask.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
        foreach (var pair in _sessions.ToArray()) { _sessions.TryRemove(pair.Key, out _); await pair.Value.DisposeAsync(); }
        _stop.Dispose(); _stop = null; _connectTask = null; State = TcpRuntimeState.Stopped;
    }
    public async Task SendAsync(string connectionId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(connectionId, out var session)) throw new InvalidOperationException("尚未连接远端主机");
        await session.SendAsync(data, ct);
    }
    public Task BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => SendAsync("client", data, ct);
    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class TcpTaskMatcher
{
    public static string? Match(string command, string? taskKey, IReadOnlyList<Persistence.TaskEntity> tasks)
    {
        if (!string.IsNullOrWhiteSpace(taskKey)) return tasks.FirstOrDefault(t => string.Equals(t.StationCode, taskKey, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, taskKey, StringComparison.OrdinalIgnoreCase))?.StationCode;
        return tasks.FirstOrDefault(t => string.Equals(t.StationCode, command, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, command, StringComparison.OrdinalIgnoreCase))?.StationCode;
    }
}

public sealed class ProjectCommunicationManager : IAsyncDisposable
{
    private sealed class ProjectRuntime(ProjectCommunicationConfig config)
    {
        public ProjectCommunicationConfig Config { get; } = config;
        public TcpSecurityValidator Security { get; } = new(TimeSpan.FromSeconds(Math.Max(1, config.MaxClockSkewSeconds)));
        public Channel<TcpFrameReceivedEventArgs> FrameChannel { get; } = Channel.CreateBounded<TcpFrameReceivedEventArgs>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
        public Task? FrameConsumer { get; set; }
        public ITcpTransport? Transport { get; set; }
        public EventHandler<TcpRawDataReceivedEventArgs>? RawDataHandler { get; set; }
        public EventHandler<TcpFrameReceivedEventArgs>? FrameHandler { get; set; }
        public EventHandler<TcpConnectionChangedEventArgs>? ConnectionHandler { get; set; }
    }

    private readonly Persistence.ProjectStationRepository _projects;
    private readonly Persistence.TaskRepository _tasks;
    private readonly ILogger<ProjectCommunicationManager>? _logger;
    private readonly string? _logDirectory;
    private readonly Persistence.CommunicationRequestStore? _requestStore;
    private readonly ConcurrentDictionary<string, string> _completed = new();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _rateWindows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    public TcpRuntimeState State => _runtimes.Values.Select(runtime => runtime.Transport?.State ?? TcpRuntimeState.Stopped)
        .OrderByDescending(state => state == TcpRuntimeState.Connected)
        .ThenByDescending(state => state == TcpRuntimeState.Listening)
        .ThenByDescending(state => state == TcpRuntimeState.Connecting || state == TcpRuntimeState.Reconnecting)
        .FirstOrDefault();
    public TcpRuntimeState GetState(string projectCode) => _runtimes.TryGetValue(projectCode, out var runtime)
        ? runtime.Transport?.State ?? TcpRuntimeState.Stopped
        : TcpRuntimeState.Stopped;
    public IReadOnlyCollection<TcpConnectionInfo> Connections => _runtimes.Values.SelectMany(runtime => runtime.Transport?.Connections ?? []).ToArray();
    public IReadOnlyCollection<TcpConnectionInfo> GetConnections(string projectCode) => _runtimes.TryGetValue(projectCode, out var runtime)
        ? runtime.Transport?.Connections ?? []
        : [];
    public event EventHandler<TcpLogEntry>? LogReceived;
    public event EventHandler? StateChanged;
    public Func<Persistence.TaskEntity, CancellationToken, Task<TcpTaskExecutionResult>>? TaskExecutor { get; set; }
    public ProjectCommunicationManager(Persistence.ProjectStationRepository projects, Persistence.TaskRepository tasks, ILogger<ProjectCommunicationManager>? logger = null, string? logDirectory = null, Persistence.CommunicationRequestStore? requestStore = null) { _projects = projects; _tasks = tasks; _logger = logger; _logDirectory = logDirectory; _requestStore = requestStore; }
    public async Task StartAsync(ProjectCommunicationConfig config, CancellationToken ct = default)
    {
        var projectCode = string.IsNullOrWhiteSpace(config.ProjectCode) ? "default" : config.ProjectCode;
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            await StopProjectCoreAsync(projectCode, ct);
            var runtime = new ProjectRuntime(config);
            _runtimes[projectCode] = runtime;
            runtime.FrameConsumer = ProcessFramesAsync(runtime, runtime.FrameChannel.Reader, ct);
            if (!config.Enabled || config.WorkMode == TcpWorkMode.Disabled)
            {
                Log("INFO", "SYS", $"[{projectCode}] TCP/IP 通讯未启用");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            runtime.Transport = config.WorkMode == TcpWorkMode.Server ? new TcpServerTransport() : new TcpClientTransport();
            runtime.RawDataHandler = (_, e) => Transport_RawDataReceived(runtime, e);
            runtime.FrameHandler = (_, e) => Transport_FrameReceived(runtime, e);
            runtime.ConnectionHandler = (_, e) => Transport_ConnectionChanged(runtime, e);
            runtime.Transport.RawDataReceived += runtime.RawDataHandler;
            runtime.Transport.FrameReceived += runtime.FrameHandler;
            runtime.Transport.ConnectionChanged += runtime.ConnectionHandler;
            try
            {
                await runtime.Transport.StartAsync(config, ct);
                Log("INFO", "SYS", $"[{projectCode}] TCP {config.WorkMode} 已启动");
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log("ERROR", "SYS", $"[{projectCode}] {ex.Message}");
                _logger?.LogError(ex, "TCP start failed");
                await StopProjectCoreAsync(projectCode, ct);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            foreach (var projectCode in _runtimes.Keys.ToArray())
                await StopProjectCoreAsync(projectCode, ct);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
    public async Task StopProjectAsync(string projectCode, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try { await StopProjectCoreAsync(projectCode, ct); }
        finally { _lifecycleGate.Release(); }
    }
    private async Task StopProjectCoreAsync(string projectCode, CancellationToken ct)
    {
        if (!_runtimes.TryRemove(projectCode, out var runtime)) return;
        runtime.FrameChannel.Writer.TryComplete();
        if (runtime.FrameConsumer is not null)
        {
            try { await runtime.FrameConsumer.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
        }
        if (runtime.Transport is not null)
        {
            if (runtime.RawDataHandler is not null) runtime.Transport.RawDataReceived -= runtime.RawDataHandler;
            if (runtime.FrameHandler is not null) runtime.Transport.FrameReceived -= runtime.FrameHandler;
            if (runtime.ConnectionHandler is not null) runtime.Transport.ConnectionChanged -= runtime.ConnectionHandler;
            await runtime.Transport.DisposeAsync();
        }
        Log("INFO", "SYS", $"[{projectCode}] TCP 已停止");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Transport_ConnectionChanged(ProjectRuntime runtime, TcpConnectionChangedEventArgs e)
    {
        Log(e.Connected ? "INFO" : "WARN", "SYS", $"[{runtime.Config.ProjectCode}] {(e.Connected ? "连接建立" : "连接断开")}: {e.Connection.ConnectionId} {e.Connection.RemoteEndpoint} {e.Error}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Transport_RawDataReceived(ProjectRuntime runtime, TcpRawDataReceivedEventArgs e)
    {
        var text = TcpMessageCodec.GetEncoding(runtime.Config).GetString(e.Data)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        Log("INFO", "RAW-RX", $"[{runtime.Config.ProjectCode}/{e.Connection.ConnectionId}] {text} ({e.Data.Length} bytes)");
    }
    private void Transport_FrameReceived(ProjectRuntime runtime, TcpFrameReceivedEventArgs e)
    {
        var text = TcpMessageCodec.GetEncoding(runtime.Config).GetString(e.Data); Log("INFO", "RX", $"[{runtime.Config.ProjectCode}/{e.Connection.ConnectionId}] {text}");
        if (!runtime.FrameChannel.Writer.TryWrite(e)) Log("WARN", "SYS", $"[{runtime.Config.ProjectCode}] TCP 接收队列已满，已丢弃报文");
    }
    private async Task ProcessFramesAsync(ProjectRuntime runtime, ChannelReader<TcpFrameReceivedEventArgs> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
                try { await DispatchAsync(runtime, frame.Connection.ConnectionId, frame.Data, frame.Connection.RemoteEndpoint); } catch (Exception ex) { Log("ERROR", "SYS", ex.Message); }
        }
        catch (OperationCanceledException) { }
    }
    public Task SendTextAsync(string connectionId, string text, CancellationToken ct = default) => SendTextAsync("default", connectionId, text, ct);
    public async Task SendTextAsync(string projectCode, string connectionId, string text, CancellationToken ct = default)
    { if (!_runtimes.TryGetValue(projectCode, out var runtime) || runtime.Transport is null) throw new InvalidOperationException("TCP 未启动"); var data = TcpMessageCodec.Encode(text, runtime.Config); await runtime.Transport.SendAsync(connectionId, data, ct); Log("INFO", "TX", $"[{projectCode}/{connectionId}] {text}"); }
    public Task BroadcastTextAsync(string text, CancellationToken ct = default) => BroadcastTextAsync("default", text, ct);
    public async Task BroadcastTextAsync(string projectCode, string text, CancellationToken ct = default)
    { if (!_runtimes.TryGetValue(projectCode, out var runtime) || runtime.Transport is null) throw new InvalidOperationException("TCP 未启动"); var data = TcpMessageCodec.Encode(text, runtime.Config); await runtime.Transport.BroadcastAsync(data, ct); Log("INFO", "TX", $"[{projectCode}/广播] {text}"); }
    private async Task SendTextAsync(ProjectRuntime runtime, string connectionId, string text, CancellationToken ct = default)
    { if (runtime.Transport is null) throw new InvalidOperationException("TCP 未启动"); var data = TcpMessageCodec.Encode(text, runtime.Config); await runtime.Transport.SendAsync(connectionId, data, ct); Log("INFO", "TX", $"[{runtime.Config.ProjectCode}/{connectionId}] {text}"); }
    private async Task DispatchAsync(ProjectRuntime runtime, string connectionId, byte[] raw, string endpoint)
    {
        var text = TcpMessageCodec.GetEncoding(runtime.Config).GetString(raw).Trim();
        if (!AllowRate(runtime, endpoint)) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "rate_limited" }); return; }
        if (runtime.Config.RequireAuthentication)
        {
            try
            {
                using var authDoc = JsonDocument.Parse(text);
                if (!runtime.Security.Validate(authDoc.RootElement, runtime.Config.SharedSecret, DateTimeOffset.UtcNow, out var authError))
                { await SendJsonAsync(runtime, connectionId, new { ok = false, code = authError }); return; }
            }
            catch (JsonException)
            { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "authentication_required" }); return; }
        }
        var tasks = await _tasks.ListAsync();
        var configured = tasks
            .Select(task => (Task: task, Trigger: ParseTrigger(task.TriggerJson, runtime.Config.ProjectCode)))
            .FirstOrDefault(pair => pair.Trigger is { Enabled: true } trigger
                && IsForCurrentProject(trigger, runtime.Config.ProjectCode)
                && Matches(trigger, text));
        if (configured.Task is not null && configured.Trigger is not null)
        {
            await ExecuteConfiguredTaskAsync(runtime, connectionId, text, configured.Task, configured.Trigger);
            return;
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "invalid_json", message = "消息不是合法 JSON" }); return; }
        using (doc)
        {
            var root = doc.RootElement;
            var command = root.TryGetProperty("command", out var c) ? c.GetString() : null;
            var requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
            if (string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase)) { await SendJsonAsync(runtime, connectionId, new { ok = true, code = "pong", message = "TCP服务正常", requestId, timestamp = DateTimeOffset.UtcNow }); return; }
            if (string.IsNullOrWhiteSpace(requestId)) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "request_id_required", message = "execute 必须提供 requestId" }); return; }
            var clientId = root.TryGetProperty("clientId", out var client) && client.ValueKind == JsonValueKind.String ? client.GetString() : endpoint;
            Persistence.CommunicationRequestEntity? persistedRequest = null;
            if (_requestStore is not null)
            {
                var persisted = await _requestStore.BeginAsync(runtime.Config.ProjectCode, clientId ?? endpoint, requestId!, command ?? "", text);
                if (persisted.State == Persistence.CommunicationRequestState.Cached && persisted.ResponseJson is not null) { await SendTextAsync(runtime, connectionId, persisted.ResponseJson); return; }
                if (persisted.State == Persistence.CommunicationRequestState.Conflict) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "request_id_conflict", requestId }); return; }
                if (persisted.State == Persistence.CommunicationRequestState.Processing) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "request_processing", requestId }); return; }
                persistedRequest = persisted.Request;
            }
            if (_completed.TryGetValue($"{runtime.Config.ProjectCode}:{requestId}", out var cached)) { await SendTextAsync(runtime, connectionId, cached); return; }
            if (!string.Equals(command, "execute", StringComparison.OrdinalIgnoreCase) && !string.Equals(command, "task", StringComparison.OrdinalIgnoreCase)) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "unsupported_command", requestId }); return; }
            var taskKey = root.TryGetProperty("task", out var t) ? t.GetString() : null;
            var matched = TcpTaskMatcher.Match(command ?? "", taskKey, tasks);
            if (matched is null) { await SendJsonAsync(runtime, connectionId, new { ok = false, code = "task_not_configured", requestId, task = taskKey }); return; }
            await SendJsonAsync(runtime, connectionId, new { ok = true, code = "accepted", requestId, data = new { task = matched, queuePosition = 0 } });
            var result = JsonSerializer.Serialize(new { ok = true, code = "completed", requestId, responseId = Guid.NewGuid().ToString("N"), data = new { task = matched, status = "received", message = "命令已匹配；请在实时检测任务中启动对应工位" } });
            _completed[$"{runtime.Config.ProjectCode}:{requestId}"] = result;
            if (persistedRequest is not null) await _requestStore!.CompleteAsync(persistedRequest.Id, result);
            await SendTextAsync(runtime, connectionId, result);
        }
    }
    private async Task ExecuteConfiguredTaskAsync(ProjectRuntime runtime, string connectionId, string text, Persistence.TaskEntity task, TaskTcpTriggerConfig trigger)
    {
        var requestId = TryReadRequestId(text) ?? Guid.NewGuid().ToString("N");
        var cacheKey = $"{runtime.Config.ProjectCode}:{requestId}";
        if (_completed.TryGetValue(cacheKey, out var cached)) { await SendTextAsync(runtime, connectionId, cached); return; }
        await SendJsonAsync(runtime, connectionId, new { ok = true, code = "accepted", requestId, data = new { task = task.Name } });
        try
        {
            if (TaskExecutor is null) throw new InvalidOperationException("TCP 任务执行器未配置");
            var result = await TaskExecutor(task, CancellationToken.None);
            var response = ApplyTemplate(trigger.ResponseTemplate, requestId, task, result);
            _completed[cacheKey] = response;
            await SendTextAsync(runtime, connectionId, response);
        }
        catch (Exception ex)
        {
            await SendJsonAsync(runtime, connectionId, new { ok = false, code = "execution_failed", requestId, task = task.Name, message = ex.Message });
        }
    }
    private static TaskTcpTriggerConfig? ParseTrigger(string? json, string? projectCode)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (document.RootElement.TryGetProperty("TcpProjectCode", out _)
                || document.RootElement.TryGetProperty("tcpProjectCode", out _))
            {
                var single = JsonSerializer.Deserialize<TaskTcpTriggerConfig>(json, options);
                return single is not null && (string.IsNullOrWhiteSpace(single.TcpProjectCode)
                    || string.Equals(single.TcpProjectCode, projectCode, StringComparison.OrdinalIgnoreCase))
                    ? single
                    : null;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, TaskTcpTriggerConfig>>(json, options);
            if (map is null || projectCode is null) return null;
            var pair = map.FirstOrDefault(item => string.Equals(item.Key, projectCode, StringComparison.OrdinalIgnoreCase));
            if (pair.Value is null) return null;
            pair.Value.TcpProjectCode = string.IsNullOrWhiteSpace(pair.Value.TcpProjectCode)
                ? pair.Key
                : pair.Value.TcpProjectCode;
            return pair.Value;
        }
        catch (JsonException) { return null; }
    }
    private static bool Matches(TaskTcpTriggerConfig trigger, string text) => trigger.MatchMode switch
    {
        MessageMatchMode.ExactText => string.Equals(text, trigger.MatchValue, StringComparison.Ordinal),
        MessageMatchMode.Prefix => text.StartsWith(trigger.MatchValue, StringComparison.Ordinal),
        MessageMatchMode.Regex => Regex.IsMatch(text, trigger.MatchValue, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        MessageMatchMode.Default => true,
        _ => false,
    };
    private static bool IsForCurrentProject(TaskTcpTriggerConfig trigger, string projectCode) =>
        string.IsNullOrWhiteSpace(trigger.TcpProjectCode)
        || string.Equals(trigger.TcpProjectCode, projectCode, StringComparison.OrdinalIgnoreCase);
    private static string? TryReadRequestId(string text)
    {
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.TryGetProperty("requestId", out var value) ? value.GetString() : null; }
        catch (JsonException) { return null; }
    }
    private static string ApplyTemplate(string template, string requestId, Persistence.TaskEntity task, TcpTaskExecutionResult result) =>
        (string.IsNullOrWhiteSpace(template) ? "{\"status\":\"{status}\"}" : template)
            .Replace("{requestId}", requestId, StringComparison.Ordinal)
            .Replace("{task}", task.Name, StringComparison.Ordinal)
            .Replace("{taskCode}", task.StationCode, StringComparison.Ordinal)
            .Replace("{status}", result.Status, StringComparison.Ordinal)
            .Replace("{decision}", result.Decision, StringComparison.Ordinal)
            .Replace("{count}", result.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{recordId}", result.RecordId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    private async Task SendJsonAsync(ProjectRuntime runtime, string connectionId, object value) => await SendTextAsync(runtime, connectionId, JsonSerializer.Serialize(value));
    private bool AllowRate(ProjectRuntime runtime, string endpoint)
    {
        var limit = Math.Max(1, runtime.Config.MaxRequestsPerMinute);
        var now = DateTimeOffset.UtcNow;
        var window = _rateWindows.GetOrAdd($"{runtime.Config.ProjectCode}:{endpoint}", _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() >= TimeSpan.FromMinutes(1)) window.Dequeue();
            if (window.Count >= limit) return false;
            window.Enqueue(now); return true;
        }
    }
    private void Log(string level, string direction, string message)
    {
        var entry = new TcpLogEntry(DateTime.Now, level, direction, "", message);
        LogReceived?.Invoke(this, entry);
        if (!string.IsNullOrWhiteSpace(_logDirectory))
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(Path.Combine(_logDirectory, $"tcp-{entry.Timestamp:yyyy-MM-dd}.log"), $"[{entry.Timestamp:O}] {entry.Level} {entry.Direction} {entry.Message}{Environment.NewLine}");
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "写入 TCP 日志失败"); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }
}
