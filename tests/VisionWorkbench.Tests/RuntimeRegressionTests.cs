using Microsoft.Extensions.Logging.Abstractions;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Cameras.Files;
using VisionWorkbench.Cameras.Hikvision;
using VisionWorkbench.Cameras.Usb;
using System.Reflection;
using VisionWorkbench.Contracts.Plugins;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Infrastructure.Workers;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class RuntimeRegressionTests
{
    [Fact]
    public async Task PauseGate_RepeatedToggleWithoutWaiter_DoesNotOverflow()
    {
        using var gate = new AsyncPauseGate();
        for (var i = 0; i < 1000; i++)
        {
            gate.Pause();
            gate.Resume();
        }
        gate.Pause();
        var wait = gate.WaitIfPausedAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        gate.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PauseGate_ResumeReleasesEveryWaiter_AndCancellationDoesNotResume()
    {
        using var gate = new AsyncPauseGate();
        gate.Pause();
        using var cancellation = new CancellationTokenSource();
        var canceled = gate.WaitIfPausedAsync(cancellation.Token);
        var first = gate.WaitIfPausedAsync(CancellationToken.None);
        var second = gate.WaitIfPausedAsync(CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.True(gate.IsPaused);
        gate.Resume();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PauseGate_DisposeReleasesPendingWaiters()
    {
        var gate = new AsyncPauseGate();
        gate.Pause();
        var wait = gate.WaitIfPausedAsync(CancellationToken.None);
        gate.Dispose();
        gate.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => wait);
    }

    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(1, 1, 2)]
    [InlineData(65536, 65536, 0)]
    public void PixelBuffers_RejectWrongLengthBeforeNativeMemoryCopy(int width, int height, int length)
    {
        var pixels = new byte[length];
        Assert.Throws<ArgumentException>(() => new VideoFrame(1, DateTimeOffset.UtcNow, width, height, pixels));
        Assert.Throws<ArgumentException>(() => TempImageStore.WritePng(pixels, width, height, "unused.png"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void PixelBuffers_RejectInvalidDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoFrame(1, DateTimeOffset.UtcNow, width, height, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => TempImageStore.WritePng([], width, height, "unused.png"));
    }

    [Fact]
    public async Task CameraCleanup_TimeoutDoesNotReleaseActiveResource_AndFaultStillCleansUp()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = 0;
        Exception? observed = null;
        var completed = await CameraSessionCleanup.ReleaseAfterLoopAsync(loop.Task, () =>
        {
            Interlocked.Increment(ref releaseCount);
            released.TrySetResult();
        }, TimeSpan.FromMilliseconds(20), error => observed = error);
        Assert.False(completed);
        Assert.Equal(0, releaseCount);
        var failure = new InvalidOperationException("fake device read failed");
        loop.TrySetException(failure);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, releaseCount);
        Assert.Equal(failure, observed);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("network")]
    [InlineData("usb")]
    [InlineData("hikvision")]
    public async Task CameraSession_BlockedCapture_DefersResourcesAndRejectsRestart(string kind)
    {
        ICameraProvider provider = kind switch
        {
            "video" => new VideoFileProvider(),
            "network" => new NetworkCameraProvider(),
            "usb" => new UsbCameraProvider(),
            _ => new HikvisionCameraProvider(),
        };
        var session = await provider.CreateSessionAsync(new CameraDescriptor
        {
            ProviderId = provider.ProviderId, DeviceId = "0", DisplayName = "fake blocked device",
        }, CancellationToken.None);
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        session.GetType().GetField("_loopTask", flags)!.SetValue(session, loop.Task);
        var cancellation = (CancellationTokenSource)session.GetType().GetField("_cts", flags)!.GetValue(session)!;
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            // A still-running native call can safely keep using its token/gate after bounded disposal returns.
            Assert.True(cancellation.Token.IsCancellationRequested);
            await session.DisposeAsync();
            Assert.Equal(CameraSessionState.Closed, session.State);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(CancellationToken.None));
        }
        finally
        {
            loop.TrySetResult();
            await session.DisposeAsync();
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (true)
        {
            try { _ = cancellation.Token; }
            catch (ObjectDisposedException) { break; }
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Worker_StartupFailure_CanBeDisposedRepeatedly()
    {
        var process = new WorkerProcess(new WorkerProcessOptions
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            Arguments = "",
            WorkingDirectory = Path.GetTempPath(),
        }, NullLogger.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => process.StartAsync(CancellationToken.None));
        await process.DisposeAsync();
        await process.DisposeAsync();
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Worker_DisposeReleasesOwnedLoggerExactlyOnce()
    {
        var lifetime = new DisposalCounter();
        var process = new WorkerProcess(new WorkerProcessOptions
        {
            ExecutablePath = "unused.exe", Arguments = "", WorkingDirectory = Path.GetTempPath(),
        }, NullLogger.Instance, lifetime);
        await process.DisposeAsync();
        await process.DisposeAsync();
        Assert.Equal(1, lifetime.Count);
    }

    [Fact]
    public async Task Worker_RequestTimeout_CoversBlockedInputPipe()
    {
        using var fixture = new ScriptWorker(readInput: false);
        await using var process = fixture.CreateProcess();
        await process.StartAndWaitHelloAsync(CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => process.RequestAsync(
            MessageType.Submit, new { data = new string('x', 1024 * 1024) },
            TimeSpan.FromMilliseconds(300), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, process.PendingRequestCount);
        await process.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Worker_HelloWait_HonorsCancellation()
    {
        using var fixture = new ScriptWorker(emitHello: false);
        await using var process = fixture.CreateProcess();
        using var cancellation = new CancellationTokenSource();
        var handshake = process.StartAndWaitHelloAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handshake.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Worker_RequestCancellation_ClearsPendingAndKeepsProtocolUsable()
    {
        using var fixture = new ScriptWorker();
        await using var process = fixture.CreateProcess();
        await process.StartAndWaitHelloAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var request = process.RequestAsync(MessageType.Submit, new { }, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, process.PendingRequestCount);
        var response = await process.RequestAsync(MessageType.Health, new { }, null, CancellationToken.None);
        Assert.Equal(MessageType.Result, response.Type);
    }

    [Fact]
    public async Task Worker_CancelDuringLargeWrite_PreservesFramingAndIgnoresLateReply()
    {
        using var fixture = new ScriptWorker(inputDelayMilliseconds: 500);
        await using var process = fixture.CreateProcess();
        await process.StartAndWaitHelloAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var canceled = process.RequestAsync(MessageType.Submit,
            new { tag = "canceled", data = new string('x', 1024 * 1024) }, null, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var response = await process.RequestAsync(MessageType.Health, new { tag = "current" }, null, CancellationToken.None);
        Assert.Equal("current", response.Payload.GetProperty("requestTag").GetString());
        Assert.Equal(0, process.PendingRequestCount);
    }

    [Fact]
    public async Task Worker_ConcurrentRequests_KeepTheirOwnCorrelations()
    {
        using var fixture = new ScriptWorker();
        await using var process = fixture.CreateProcess();
        await process.StartAndWaitHelloAsync(CancellationToken.None);
        var first = process.RequestAsync(MessageType.Submit, new { tag = "first" }, null, CancellationToken.None);
        var second = process.RequestAsync(MessageType.Health, new { tag = "second" }, null, CancellationToken.None);
        var results = await Task.WhenAll(first, second);
        Assert.Equal("first", results[0].Payload.GetProperty("requestTag").GetString());
        Assert.Equal("second", results[1].Payload.GetProperty("requestTag").GetString());
    }

    [Fact]
    public async Task Worker_MalformedHelloAndThrowingEventHandlers_DoNotStopReaders()
    {
        using var fixture = new ScriptWorker();
        await using var process = fixture.CreateProcess();
        var stderr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EventReceived += (_, _) => throw new InvalidOperationException("event observer failed");
        process.StderrLine += (_, _) => throw new InvalidOperationException("stderr observer failed");
        process.StderrLine += (_, _) => stderr.TrySetResult();
        await process.StartAndWaitHelloAsync(CancellationToken.None);
        var response = await process.RequestAsync(MessageType.Health, new { }, null, CancellationToken.None);
        Assert.Equal(MessageType.Result, response.Type);
        await stderr.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, process.GarbageLineCount);
    }

    [Fact]
    public async Task AlgorithmSession_RejectsFailedReadyPayload()
    {
        using var fixture = new ScriptWorker();
        var process = fixture.CreateProcess();
        await using var session = new WorkerAlgorithmSession(process, new PluginManifest(), NullLogger.Instance);
        await process.StartAndWaitHelloAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<AlgorithmFaultException>(() =>
            session.InitializeAsync(new AlgorithmInitialization(), CancellationToken.None));
        Assert.Equal(WorkerErrorCodes.ModelLoadFailed, failure.ErrorCode);
        Assert.Equal(AlgorithmSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ImageFolder_DirectoryRemovedAfterOpen_ReportsFaultAndCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vw-camera-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var session = await new ImageFolderProvider().CreateSessionAsync(new CameraDescriptor
        {
            ProviderId = ImageFolderProvider.ProviderIdValue,
            DeviceId = directory,
            DisplayName = "test",
        }, CancellationToken.None);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CameraFault? fault = null;
        session.Faulted += (_, e) => fault = e.Fault;
        session.Completed += (_, _) => completed.TrySetResult();
        await session.OpenAsync(new CameraOpenOptions(), CancellationToken.None);
        Directory.Delete(directory);
        await session.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(fault);
        Assert.Equal(CameraSessionState.Faulted, session.State);
    }

    private sealed class DisposalCounter : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }

    // Windows PowerShell is part of the target OS; this protocol fixture needs no Python/model installation.
    private sealed class ScriptWorker : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vw-worker-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;

        public ScriptWorker(bool emitHello = true, bool readInput = true, int inputDelayMilliseconds = 0)
        {
            Directory.CreateDirectory(_directory);
            _script = Path.Combine(_directory, "worker.ps1");
            File.WriteAllText(_script, (emitHello ? """
                [Console]::Out.WriteLine('{"type":"hello","messageId":"bad","payload":{"protocolVersion":17}}')
                [Console]::Out.WriteLine('{"type":"hello","messageId":"hello","payload":{"protocolVersion":"1.0"}}')
                """ : "") + "\n" + (readInput ? $"Start-Sleep -Milliseconds {inputDelayMilliseconds}\n" : "Start-Sleep -Seconds 60\n") + """
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    $request = $line | ConvertFrom-Json
                    if ($request.type -eq 'shutdown') { break }
                    if ($request.type -eq 'submit') { $pendingSubmit = $request; continue }
                    if ($null -ne $pendingSubmit) {
                        $late = @{ type = 'result'; correlationId = $pendingSubmit.messageId; payload = @{ requestTag = $pendingSubmit.payload.tag } }
                        [Console]::Out.WriteLine(($late | ConvertTo-Json -Compress -Depth 5))
                        $pendingSubmit = $null
                    }
                    [Console]::Out.WriteLine('{"type":"event","payload":{}}')
                    [Console]::Error.WriteLine('worker diagnostic')
                    $type = if ($request.type -eq 'initialize') { 'ready' } else { 'result' }
                    $response = @{ type = $type; correlationId = $request.messageId; payload = @{ requestTag = $request.payload.tag; success = $false; errorCode = 'MODEL_LOAD_FAILED'; error = 'test model failed' } }
                    [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 5))
                }
                """);
        }

        public WorkerProcess CreateProcess() => new(new WorkerProcessOptions
        {
            ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{_script}\"",
            WorkingDirectory = _directory,
            HelloTimeout = TimeSpan.FromSeconds(15),
            RequestTimeout = TimeSpan.FromSeconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        }, NullLogger.Instance);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
