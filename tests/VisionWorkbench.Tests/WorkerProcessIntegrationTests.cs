using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Infrastructure.Workers;
using Xunit;
using Xunit.Abstractions;

namespace VisionWorkbench.Tests;

/// <summary>真进程集成测试：C# 宿主 ↔ Python Worker 的完整协议闭环。</summary>
public sealed class WorkerProcessIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static WorkerProcessOptions SampleCounterOptions() => new()
    {
        ExecutablePath = TestPaths.VenvPython,
        Arguments = $"\"{Path.Combine(TestPaths.SampleCounterPlugin, "worker.py")}\"",
        WorkingDirectory = TestPaths.SampleCounterPlugin,
        HelloTimeout = TimeSpan.FromSeconds(20),
        ReadyTimeout = TimeSpan.FromSeconds(20),
        RequestTimeout = TimeSpan.FromSeconds(20),
    };

    [Fact]
    public async Task Hello_Initialize_Submit_Result_Roundtrip()
    {
        if (!TestPaths.VenvExists)
        {
            return; // 环境无 Python 时优雅跳过
        }

        await using var process = new WorkerProcess(SampleCounterOptions(), NullLogger.Instance);
        process.StderrLine += (_, line) => _output.WriteLine("[stderr] " + line);
        try
        {
            var ready = await process.StartAndInitializeAsync(
                new InitializePayload
                {
                    ExecutionProvider = "cpu",
                    Settings = JsonDocument.Parse(
                        """{"minAreaPx": 120, "thresholdC": -12}""").RootElement,
                },
                CancellationToken.None);
            Assert.True(ready.Success);

            // img_02_n01.png 的真值为 1 个目标
            var imagePath = Path.Combine(TestPaths.SamplesStaticCount, "img_02_n01.png");
            Assert.True(File.Exists(imagePath), $"测试图片缺失: {imagePath}");

            var response = await process.RequestAsync(
                MessageType.Submit,
                new { inputId = "test-input-1", imagePath, frameSequence = 1L },
                timeout: TimeSpan.FromSeconds(20),
                CancellationToken.None);

            Assert.Equal(MessageType.Result, response.Type);
            var output = ProtocolMessage.DeserializePayload<AlgorithmOutput>(response.Payload);
            Assert.NotNull(output);
            Assert.Equal("test-input-1", output!.InputId);
            Assert.Single(output.Detections);
            Assert.Equal(1, output.GetCount());
            Assert.Equal(1, output.Metrics.First(m => m.Name == "count").Value, 5);
            Assert.True(output.Performance?.TotalMs >= 0);

            await process.ShutdownAsync();
            Assert.True(process.HasExited);
        }
        catch (Exception)
        {
            _output.WriteLine($"[diag] garbage={process.GarbageLineCount} pending={process.PendingRequestCount} exited={process.HasExited}");
            throw;
        }
    }

    [Fact]
    public async Task Error_Response_Maps_To_Fault_Exception()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        await using var process = new WorkerProcess(SampleCounterOptions(), NullLogger.Instance);
        await process.StartAndInitializeAsync(new InitializePayload(), CancellationToken.None);

        // imagePath 为空 → Worker 返回 INPUT_INVALID
        var ex = await Assert.ThrowsAsync<AlgorithmFaultException>(() =>
            process.RequestAsync(
                MessageType.Submit,
                new { inputId = "bad", imagePath = "", frameSequence = 0L },
                timeout: TimeSpan.FromSeconds(20),
                CancellationToken.None));
        Assert.Equal(WorkerErrorCodes.InputInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task Non_Json_Stdout_Counted_As_Garbage()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        var options = SampleCounterOptions();
        options = options with
        {
            Arguments = "-c \"print('debug line 1'); import time; time.sleep(60)\"",
        };
        await using var process = new WorkerProcess(options, NullLogger.Instance);
        await process.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        Assert.True(process.GarbageLineCount >= 1); // PLG-007
    }

    [Fact]
    public async Task Worker_Crash_Fails_Pending_And_Raises_Exited()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        var options = SampleCounterOptions() with
        {
            Arguments = "-c \"import os; print('bye', flush=True); os._exit(3)\"",
        };
        await using var process = new WorkerProcess(options, NullLogger.Instance);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, code) => exited.TrySetResult(code);
        await process.StartAsync(CancellationToken.None);

        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, code);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Submit_Timeout_Throws()
    {
        if (!TestPaths.VenvExists)
        {
            return;
        }

        // Worker 收到请求但不回复（sleep）→ 宿主超时（PLG-010）
        var options = SampleCounterOptions() with
        {
            Arguments = "-c \"import sys, time; print('{\\\"type\\\":\\\"hello\\\",\\\"messageId\\\":\\\"m1\\\",\\\"payload\\\":{\\\"pluginId\\\":\\\"x\\\",\\\"workerVersion\\\":\\\"1\\\",\\\"protocolVersion\\\":\\\"1.0\\\"}}', flush=True); time.sleep(120)\"",
        };
        await using var process = new WorkerProcess(options with { HelloTimeout = TimeSpan.FromSeconds(5) },
            NullLogger.Instance);
        await process.StartAndWaitHelloAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            process.RequestAsync(
                MessageType.Submit,
                new { inputId = "slow", imagePath = "x.png", frameSequence = 0L },
                timeout: TimeSpan.FromSeconds(2),
                CancellationToken.None));
    }
}
