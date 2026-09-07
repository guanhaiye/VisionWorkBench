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
    bool Resume,
    bool ExportTensorRt,
    string? PauseFilePath = null);

public sealed record Yolo11TrainingProgress(
    string Event,
    int Epoch,
    int TotalEpochs,
    double? TrainLoss,
    double? ValLoss,
    string Message);

public sealed record Yolo11TrainingResult(string ModelPath, string RunDirectory, string? EnginePath);
public sealed record Yolo11GpuMemory(bool Available, long FreeBytes, long TotalBytes);

/// <summary>运行 YOLO11 训练 worker，并把每轮训练事件转发给桌面端。</summary>
public sealed class Yolo11TrainingService : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _python;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _pauseFilePath;
    private volatile bool _paused;

    public Yolo11TrainingService(string workersRoot, string? configuredPython)
    {
        var yoloDirectory = Path.Combine(workersRoot, "yolo11");
        _workingDirectory = yoloDirectory;
        _scriptPath = Path.Combine(yoloDirectory, "train_worker.py");
        _python = PythonProcessSupport.ResolvePython(configuredPython, yoloDirectory, workersRoot);
    }

    public bool IsModelAvailable(string modelPath) =>
        File.Exists(Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(_workingDirectory, modelPath));

    public bool IsRunning => _process is { HasExited: false };

    public bool IsPaused => _paused && IsRunning;

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
            _paused = false;
            var pauseFilePath = string.IsNullOrWhiteSpace(request.PauseFilePath)
                ? Path.Combine(Path.GetTempPath(), $"visionworkbench-yolo11-{Guid.NewGuid():N}.pause")
                : request.PauseFilePath;
            _pauseFilePath = pauseFilePath;
            TryDeletePauseFile();
            if (!process.Start()) throw new InvalidOperationException("无法启动 YOLO11 训练 worker。");

            using var cancellationRegistration = cancellationToken.Register(() => PythonProcessSupport.TryKill(process));
            var workerRequest = request with { PauseFilePath = pauseFilePath };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                workerRequest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            string? modelPath = null;
            string? runDirectory = null;
            string? enginePath = null;
            string? workerError = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
            {
                if (!PythonProcessSupport.TryParseEvent(line, out var json)) continue;
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
                    enginePath = ReadString(json, "enginePath");
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
            return new Yolo11TrainingResult(modelPath, runDirectory, enginePath);
        }
        finally
        {
            if (_process is { HasExited: false } running) PythonProcessSupport.TryKill(running);
            _process?.Dispose();
            _process = null;
            TryDeletePauseFile();
            _pauseFilePath = null;
            _paused = false;
            _gate.Release();
        }
    }

    public Task CancelAsync()
    {
        TryDeletePauseFile();
        if (_process is { HasExited: false } process)
        {
            ProcessPauseController.TryResume(process);
            PythonProcessSupport.TryKill(process);
        }
        _paused = false;
        return Task.CompletedTask;
    }

    public Task<bool> PauseAsync()
    {
        if (_process is not { HasExited: false } process || _paused)
            return Task.FromResult(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_pauseFilePath))
            {
                File.WriteAllText(_pauseFilePath, "pause");
                _paused = true;
            }
            else
            {
                _paused = ProcessPauseController.TrySuspend(process);
            }
        }
        catch
        {
            _paused = false;
        }
        return Task.FromResult(_paused);
    }

    public Task<bool> ResumeAsync()
    {
        if (_process is not { HasExited: false } process || !_paused)
            return Task.FromResult(false);
        var resumed = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(_pauseFilePath))
            {
                TryDeletePauseFile();
                resumed = true;
            }
            else
            {
                resumed = ProcessPauseController.TryResume(process);
            }
        }
        catch { }
        if (resumed) _paused = false;
        return Task.FromResult(resumed);
    }

    public async Task<Yolo11GpuMemory> QueryGpuMemoryAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _python,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "import json, torch; " +
            "ok=torch.cuda.is_available(); " +
            "free,total=torch.cuda.mem_get_info(0) if ok else (0,0); " +
            "print(json.dumps({'available':ok,'freeBytes':int(free),'totalBytes':int(total)}))");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return new Yolo11GpuMemory(false, 0, 0);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return new Yolo11GpuMemory(false, 0, 0);
        try
        {
            using var json = JsonDocument.Parse(output.Trim());
            var root = json.RootElement;
            return new Yolo11GpuMemory(
                root.GetProperty("available").GetBoolean(),
                root.GetProperty("freeBytes").GetInt64(),
                root.GetProperty("totalBytes").GetInt64());
        }
        catch (JsonException)
        {
            return new Yolo11GpuMemory(false, 0, 0);
        }
    }

    public void Dispose()
    {
        TryDeletePauseFile();
        if (_process is { HasExited: false } process)
        {
            ProcessPauseController.TryResume(process);
            PythonProcessSupport.TryKill(process);
        }
        _paused = false;
        _gate.Dispose();
    }

    private void TryDeletePauseFile()
    {
        if (string.IsNullOrWhiteSpace(_pauseFilePath)) return;
        try
        {
            if (File.Exists(_pauseFilePath)) File.Delete(_pauseFilePath);
        }
        catch { }
    }

    private static int ReadInt(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static double? ReadDouble(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var value) ? value : null;

    private static string? ReadString(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var property) ? property.GetString() : null;

}
