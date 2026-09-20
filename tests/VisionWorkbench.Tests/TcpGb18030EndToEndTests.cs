using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>
/// 验证 TCP 项目配置 gb18030 时，软件发出的 accepted/completed JSON 中中文任务名按 GBK 字节原样输出
/// （不转义为 \uXXXX），使用 GBK 解码的对端可直接读到中文。真实 loopback TCP。
/// </summary>
public sealed class TcpGb18030EndToEndTests : IDisposable
{
    private const string ClientId = "tcp-gbk-client";
    private const string TaskName = "语义分割测试";
    private readonly string _root;
    private readonly VisionDbContextFactory _factory;
    private readonly TaskRepository _tasks;
    private readonly ProjectCommunicationManager _manager;
    private readonly int _port;
    private readonly Encoding _gbk;

    public TcpGb18030EndToEndTests()
    {
        // 触碰 TcpMessageCodec 以触发代码页 EncodingProvider 注册。
        _gbk = TcpMessageCodec.GetEncoding(new ProjectCommunicationConfig { Encoding = "gb18030" });
        _root = Path.Combine(Path.GetTempPath(), "vw-tcp-gbk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = VisionDbContextFactory.Create(Path.Combine(_root, "visionworkbench.db"));
        _tasks = new TaskRepository(_factory);
        _manager = new ProjectCommunicationManager(
            new ProjectStationRepository(_factory), _tasks, logger: null, logDirectory: null, requestStore: null);
        _port = GetFreePort();
    }

    public void Dispose()
    {
        try { _manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Gb18030Profile_SendsChineseTaskNameAsGbkBytes_WithoutUnicodeEscaping()
    {
        const string project = "p-gbk";
        await _manager.StartAsync(new ProjectCommunicationConfig
        {
            ProjectCode = project,
            Name = project,
            Enabled = true,
            WorkMode = TcpWorkMode.Server,
            ListenAddress = "127.0.0.1",
            ListenPort = _port,
            Encoding = "gb18030",
            FrameMode = TcpFrameMode.Line,
            MessageTerminator = "\\n",
            MaxRequestsPerMinute = 1000,
            IdleTimeoutSeconds = 0,
            StationMaxConcurrency = 1,
            StationQueueLength = 1,
            StationExecutionTimeoutSeconds = 30,
        });

        var task = await _tasks.SaveAsync(new TaskEntity
        {
            Name = TaskName,
            StationCode = "station-gbk",
            CameraProviderId = "files",
            CameraDeviceId = ".",
            PluginId = "sample-counter",
        });
        _manager.TaskExecutor = (request, ct) =>
            Task.FromResult(new TcpTaskExecutionResult("completed", 1, "ok", 0));

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using var reader = new LineReader(client.GetStream(), _gbk);

        var payload = $"{{\"command\":\"execute\",\"requestId\":\"req-gbk-1\",\"task\":\"{task.StationCode}\",\"clientId\":\"{ClientId}\"}}\n";
        await client.GetStream().WriteAsync(_gbk.GetBytes(payload));

        using var acceptedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = await reader.ReadLineAsync(acceptedTimeout.Token);
        Assert.Contains("accepted", accepted);
        Assert.Contains(TaskName, accepted);
        Assert.DoesNotContain(@"\u", accepted);

        using var completedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completed = await reader.ReadLineAsync(completedTimeout.Token);
        Assert.Contains("completed", completed);
        Assert.Contains(TaskName, completed);
        Assert.DoesNotContain(@"\u", completed);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class LineReader(Stream stream, Encoding encoding) : IDisposable
    {
        private readonly MemoryStream _buffer = new();

        public void Dispose() => _buffer.Dispose();

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                var bytes = _buffer.ToArray();
                var newline = Array.IndexOf(bytes, (byte)'\n');
                if (newline >= 0)
                {
                    var line = encoding.GetString(bytes, 0, newline).TrimEnd('\r');
                    _buffer.SetLength(0);
                    _buffer.Write(bytes, newline + 1, bytes.Length - newline - 1);
                    return line;
                }
                var chunk = new byte[8192];
                var read = await stream.ReadAsync(chunk.AsMemory(), ct);
                if (read == 0) throw new IOException("连接关闭，未收到完整响应");
                _buffer.Write(chunk, 0, read);
            }
        }
    }
}
