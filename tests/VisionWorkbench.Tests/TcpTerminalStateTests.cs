using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>
/// TCP 请求终态闭环测试：验证已 Begin 的请求在 execution_unavailable、station_queue_full、
/// 项目停止取消等分支都会持久化为 completed 终态，且重发同一 requestId 不会停留在 processing。
/// 使用真实 loopback TCP + 真实 CommunicationRequestStore(SQLite)。
/// </summary>
public sealed class TcpTerminalStateTests : IDisposable
{
    private const string ClientId = "tcp-terminal-client";
    private readonly string _root;
    private readonly VisionDbContextFactory _factory;
    private readonly CommunicationRequestStore _store;
    private readonly TaskRepository _tasks;
    private readonly ProjectStationRepository _projects;
    private readonly ProjectCommunicationManager _manager;
    private readonly int _port;

    public TcpTerminalStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vw-tcp-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = VisionDbContextFactory.Create(Path.Combine(_root, "visionworkbench.db"));
        _store = new CommunicationRequestStore(_factory);
        _tasks = new TaskRepository(_factory);
        _projects = new ProjectStationRepository(_factory);
        _manager = new ProjectCommunicationManager(_projects, _tasks, logger: null, logDirectory: null, requestStore: _store);
        _port = GetFreePort();
    }

    public void Dispose()
    {
        try { _manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task TaskExecutorNotSet_CompletesPersistedRequestWithExecutionUnavailable()
    {
        const string project = "p-unavail";
        await StartAsync(project, maxConcurrency: 1, queueLength: 1);
        var task = await CreateTaskAsync();
        _manager.TaskExecutor = null;

        using var client = await ConnectAsync();
        using var reader = new LineReader(client.GetStream());
        const string requestId = "req-unavail";

        var response = await SendAndReadAsync(client, reader, ExecuteJson(requestId, task.StationCode));
        Assert.Contains("execution_unavailable", response);

        var json = await WaitForTerminalAsync(project, requestId, "execution_unavailable");
        Assert.Contains(requestId, json);
        Assert.Contains("execution_unavailable", json);

        // 重发同一 requestId 应返回持久化终态，而不是 request_processing
        var retry = await SendAndReadAsync(client, reader, ExecuteJson(requestId, task.StationCode));
        Assert.Contains("execution_unavailable", retry);
        Assert.DoesNotContain("request_processing", retry);
    }

    [Fact]
    public async Task StationQueueFull_CompletesPersistedRequestWithStationQueueFull()
    {
        const string project = "p-queue";
        // 并发 1 + 队列 1：第三个请求必然 QueueFull
        await StartAsync(project, maxConcurrency: 1, queueLength: 1);
        var task = await CreateTaskAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.TaskExecutor = async (request, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return new TcpTaskExecutionResult("completed", 1, "ok", 0);
        };

        using var client = await ConnectAsync();
        using var reader = new LineReader(client.GetStream());

        var first = await SendAndReadAsync(client, reader, ExecuteJson("req-q-a", task.StationCode));
        Assert.Contains("accepted", first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = await SendAndReadAsync(client, reader, ExecuteJson("req-q-b", task.StationCode));
        Assert.Contains("accepted", second);

        var third = await SendAndReadAsync(client, reader, ExecuteJson("req-q-c", task.StationCode));
        Assert.Contains("station_queue_full", third);

        var json = await WaitForTerminalAsync(project, "req-q-c", "station_queue_full");
        Assert.Contains("req-q-c", json);

        // 重发同一 requestId 应返回持久化终态，而不是 request_processing
        var retry = await SendAndReadAsync(client, reader, ExecuteJson("req-q-c", task.StationCode));
        Assert.Contains("station_queue_full", retry);
        Assert.DoesNotContain("request_processing", retry);

        release.TrySetResult(true);
    }

    [Fact]
    public async Task ProjectStop_CancelsExecutionAndCompletesPersistedRequestWithProjectStopped()
    {
        const string project = "p-stop";
        await StartAsync(project, maxConcurrency: 1, queueLength: 1);
        var task = await CreateTaskAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.TaskExecutor = async (request, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return new TcpTaskExecutionResult("completed", 1, "ok", 0);
        };

        using var client = await ConnectAsync();
        using var reader = new LineReader(client.GetStream());
        const string requestId = "req-stop";

        var first = await SendAndReadAsync(client, reader, ExecuteJson(requestId, task.StationCode));
        Assert.Contains("accepted", first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await _manager.StopProjectAsync(project);

        var json = await WaitForTerminalAsync(project, requestId, "project_stopped");
        Assert.Contains(requestId, json);
        Assert.Contains("project_stopped", json);
    }

    [Fact]
    public async Task UnsupportedCommand_DoesNotCreateProcessingRecord()
    {
        const string project = "p-badcmd";
        await StartAsync(project, maxConcurrency: 1, queueLength: 1);
        await CreateTaskAsync();

        using var client = await ConnectAsync();
        using var reader = new LineReader(client.GetStream());
        const string requestId = "req-bad";

        var response = await SendAndReadAsync(client, reader,
            $"{{\"command\":\"reboot\",\"requestId\":\"{requestId}\",\"clientId\":\"{ClientId}\"}}");
        Assert.Contains("unsupported_command", response);

        await Task.Delay(200);
        var request = await GetRequestAsync(project, requestId);
        Assert.Null(request); // BeginAsync 未发生，不应留下 processing 记录
    }

    private async Task StartAsync(string projectCode, int maxConcurrency, int queueLength)
    {
        await _manager.StartAsync(new ProjectCommunicationConfig
        {
            ProjectCode = projectCode,
            Name = projectCode,
            Enabled = true,
            WorkMode = TcpWorkMode.Server,
            ListenAddress = "127.0.0.1",
            ListenPort = _port,
            FrameMode = TcpFrameMode.Line,
            MessageTerminator = "\\n",
            MaxRequestsPerMinute = 1000,
            IdleTimeoutSeconds = 0,
            StationMaxConcurrency = maxConcurrency,
            StationQueueLength = queueLength,
            StationExecutionTimeoutSeconds = 30,
        });
    }

    private async Task<TaskEntity> CreateTaskAsync(string stationCode = "station-001") =>
        await _tasks.SaveAsync(new TaskEntity
        {
            Name = $"tcp-terminal-{Guid.NewGuid():N}"[..24],
            StationCode = stationCode,
            CameraProviderId = "files",
            CameraDeviceId = ".",
            PluginId = "sample-counter",
        });

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _port);
        return client;
    }

    private static string ExecuteJson(string requestId, string stationCode) =>
        $"{{\"command\":\"execute\",\"requestId\":\"{requestId}\",\"task\":\"{stationCode}\",\"clientId\":\"{ClientId}\"}}";

    private static async Task<string> SendAndReadAsync(TcpClient client, LineReader reader, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        await client.GetStream().WriteAsync(bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadLineAsync(timeout.Token);
    }

    private async Task<CommunicationRequestEntity?> GetRequestAsync(string projectCode, string requestId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.CommunicationRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectCode == projectCode && r.ClientId == ClientId && r.RequestId == requestId);
    }

    private async Task<string> WaitForTerminalAsync(string projectCode, string requestId, string expectedCode,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var request = await GetRequestAsync(projectCode, requestId);
            if (request is { Status: "completed" } && request.ResponseJson is { } json && json.Contains(expectedCode))
                return json;
            await Task.Delay(50);
        }
        var latest = await GetRequestAsync(projectCode, requestId);
        throw new Xunit.Sdk.XunitException(
            $"请求 {requestId} 未在超时内达到终态 {expectedCode}；Status={latest?.Status}，Response={latest?.ResponseJson}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class LineReader(Stream stream) : IDisposable
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
                    var line = Encoding.UTF8.GetString(bytes, 0, newline).TrimEnd('\r');
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
