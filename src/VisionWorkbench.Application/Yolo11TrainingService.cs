using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record Yolo11TrainingRequest(
    string DataYaml,
    string TaskType,
    string ModelPath,
    int Epochs,
    int BatchSize,
    int ImageSize,
    string Device,
    string OutputDirectory,
    string RunName,
    bool Resume);

public sealed record Yolo11TrainingProgress(
    string Event,
    int Epoch,
    int TotalEpochs,
    double? TrainLoss,
    double? ValLoss,
    string Message);

public sealed record Yolo11TrainingResult(string ModelPath, string RunDirectory);

/// <summary>运行 YOLO11 训练 worker，并把每轮训练事件转发给桌面端。</summary>
public sealed class Yolo11TrainingService : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _python;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public Yolo11TrainingService(string workersRoot, string? configuredPython)
    {
        var yoloDirectory = Path.Combine(workersRoot, "yolo11");
        _scriptPath = Path.Combine(yoloDirectory, "train_worker.py");
        _python = ResolvePython(configuredPython, yoloDirectory, workersRoot);
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task<Yolo11TrainingResult> RunAsync(
        Yolo11TrainingRequest request,
        IProgress<Yolo11TrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_scriptPath))
                throw new FileNotFoundException("YOLO11 训练 worker 不存在", _scriptPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = _python,
                WorkingDirectory = Path.GetDirectoryName(_scriptPath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add(_scriptPath);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process = process;
            if (!process.Start()) throw new InvalidOperationException("无法启动 YOLO11 训练 worker。");

            using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            string? modelPath = null;
            string? runDirectory = null;
            string? workerError = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
            {
                if (!TryParseEvent(line, out var json)) continue;
                var eventName = json.RootElement.TryGetProperty("event", out var eventProperty)
                    ? eventProperty.GetString() ?? "log"
                    : "log";
                var message = json.RootElement.TryGetProperty("message", out var messageProperty)
                    ? messageProperty.GetString() ?? ""
                    : "";
                var epoch = ReadInt(json, "epoch");
                var totalEpochs = ReadInt(json, "totalEpochs");
                var trainLoss = ReadDouble(json, "trainLoss");
                var valLoss = ReadDouble(json, "valLoss");
                if (eventName == "completed")
                {
                    modelPath = ReadString(json, "modelPath");
                    runDirectory = ReadString(json, "runDirectory");
                }
                if (eventName == "error") workerError = message;
                progress?.Report(new Yolo11TrainingProgress(
                    eventName, epoch, totalEpochs, trainLoss, valLoss, message));
            }

            var errorOutput = await errorTask;
            await process.WaitForExitAsync(cancellationToken);
            if (workerError is not null)
                throw new InvalidOperationException(workerError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                    ? $"YOLO11 训练进程退出，代码 {process.ExitCode}"
                    : errorOutput.Trim());
            if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(runDirectory))
                throw new InvalidOperationException("YOLO11 训练未返回模型保存路径。");
            return new Yolo11TrainingResult(modelPath, runDirectory);
        }
        finally
        {
            if (_process is { HasExited: false } running) TryKill(running);
            _process?.Dispose();
            _process = null;
            _gate.Release();
        }
    }

    public Task CancelAsync()
    {
        if (_process is { HasExited: false } process) TryKill(process);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_process is { HasExited: false } process) TryKill(process);
        _gate.Dispose();
    }

    private static bool TryParseEvent(string line, out JsonDocument json)
    {
        try
        {
            json = JsonDocument.Parse(line);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("event", out _);
        }
        catch (JsonException)
        {
            json = null!;
            return false;
        }
    }

    private static int ReadInt(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static double? ReadDouble(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var value) ? value : null;

    private static string? ReadString(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static string ResolvePython(string? configured, string yoloDirectory, string workersRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var candidates = new[]
        {
            Path.Combine(yoloDirectory, ".venv", "Scripts", "python.exe"),
            Path.Combine(workersRoot, ".venv", "Scripts", "python.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? "python";
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }
}
