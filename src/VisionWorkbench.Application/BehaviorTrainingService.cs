using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record BehaviorTrainingRequest(
    string DatasetPath,
    string PoseModelPath,
    int Epochs,
    int BatchSize,
    int SequenceLength,
    string Device,
    string OutputDirectory,
    string RunName,
    string? ResumeModelPath);

public sealed record BehaviorTrainingProgress(
    string Event,
    int Epoch,
    int TotalEpochs,
    double? TrainLoss,
    double? ValLoss,
    double? Accuracy,
    string Message);

public sealed record BehaviorTrainingResult(string ModelPath, string RunDirectory, string ClassesPath);

/// <summary>行为时序分类训练服务。训练过程运行在 YOLO11 虚拟环境中，不阻塞 WPF UI。</summary>
public sealed class BehaviorTrainingService : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _python;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public BehaviorTrainingService(string workersRoot, string? configuredPython)
    {
        var yoloDirectory = Path.Combine(workersRoot, "yolo11");
        _scriptPath = Path.Combine(yoloDirectory, "behavior_train_worker.py");
        _python = ResolvePython(configuredPython, yoloDirectory, workersRoot);
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task<BehaviorTrainingResult> RunAsync(
        BehaviorTrainingRequest request,
        IProgress<BehaviorTrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_scriptPath))
                throw new FileNotFoundException("行为训练 worker 不存在。", _scriptPath);

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
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.ArgumentList.Add(_scriptPath);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process = process;
            if (!process.Start()) throw new InvalidOperationException("无法启动行为训练 worker。");

            using var registration = cancellationToken.Register(() => TryKill(process));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            string? modelPath = null;
            string? runDirectory = null;
            string? classesPath = null;
            string? workerError = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
            {
                if (!TryParseEvent(line, out var json)) continue;
                var root = json.RootElement;
                var eventName = ReadString(root, "event") ?? "log";
                var message = ReadString(root, "message") ?? "";
                if (eventName == "epoch")
                {
                    progress?.Report(new BehaviorTrainingProgress(
                        eventName, ReadInt(root, "epoch"), ReadInt(root, "totalEpochs"),
                        ReadDouble(root, "trainLoss"), ReadDouble(root, "valLoss"),
                        ReadDouble(root, "accuracy"), message));
                }
                else
                {
                    if (eventName == "completed")
                    {
                        modelPath = ReadString(root, "modelPath");
                        runDirectory = ReadString(root, "runDirectory");
                        classesPath = ReadString(root, "classesPath");
                    }
                    if (eventName == "error") workerError = message;
                    progress?.Report(new BehaviorTrainingProgress(eventName, 0, 0, null, null, null, message));
                }
            }

            var errorOutput = await errorTask;
            await process.WaitForExitAsync(cancellationToken);
            if (workerError is not null) throw new InvalidOperationException(workerError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                    ? $"行为训练进程退出，代码 {process.ExitCode}" : errorOutput.Trim());
            if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(runDirectory) || string.IsNullOrWhiteSpace(classesPath))
                throw new InvalidOperationException("行为训练未返回模型保存路径。");
            return new BehaviorTrainingResult(modelPath, runDirectory, classesPath);
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

    private static bool TryParseEvent(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("event", out _);
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static int ReadInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double? ReadDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
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
