using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record Atu5TrainingRequest(
    string ManifestPath,
    string PretrainedPath,
    int Epochs,
    int BatchSize,
    int ImageSize,
    string Device,
    string OutputDirectory,
    string RunName,
    bool ExportTensorRt,
    string? PauseFilePath = null);

/// <summary>ATU5/FPN 语义分割 Python 训练 Worker。</summary>
public sealed class Atu5TrainingService : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _workingDirectory;
    private readonly string _python;
    private Process? _process;
    private string? _pauseFilePath;
    private volatile bool _paused;

    public Atu5TrainingService(string workersRoot, string? configuredPython)
    {
        var directory = Path.Combine(workersRoot, "atu5");
        _workingDirectory = directory;
        _scriptPath = Path.Combine(directory, "train_worker.py");
        _python = PythonProcessSupport.ResolvePython(configuredPython, directory,
            Path.Combine(workersRoot, "yolo11"), workersRoot);
    }

    public bool IsRunning => TryGetRunningProcess(out _);

    public bool IsPaused => _paused && IsRunning;

    public bool IsModelAvailable(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return false;
        var resolvedPath = Path.IsPathRooted(modelPath)
            ? modelPath
            : Path.Combine(_workingDirectory, modelPath);
        return File.Exists(resolvedPath);
    }

    public async Task<Yolo11TrainingResult> RunAsync(Atu5TrainingRequest request,
        IProgress<Yolo11TrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_scriptPath)) throw new FileNotFoundException("ATU5 训练 Worker 不存在。", _scriptPath);
        var info = new ProcessStartInfo
        {
            FileName = _python, WorkingDirectory = Path.GetDirectoryName(_scriptPath)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        // Python on Windows may choose the system code page for redirected
        // streams. Force UTF-8 so Chinese training messages remain readable.
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        info.ArgumentList.Add(_scriptPath);
        using var process = new Process { StartInfo = info };
        _process = process;
        _paused = false;
        var pauseFilePath = string.IsNullOrWhiteSpace(request.PauseFilePath)
            ? Path.Combine(Path.GetTempPath(), $"visionworkbench-atu5-{Guid.NewGuid():N}.pause")
            : request.PauseFilePath;
        _pauseFilePath = pauseFilePath;
        TryDeletePauseFile();
        if (!process.Start()) throw new InvalidOperationException("无法启动 ATU5 训练 Worker。");
        using var registration = cancellationToken.Register(() => PythonProcessSupport.TryKill(process));
        var workerRequest = request with { PauseFilePath = pauseFilePath };
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(workerRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var eventName = root.TryGetProperty("event", out var e) ? e.GetString() ?? "log" : "log";
                var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                if (eventName == "completed")
                {
                    modelPath = ReadString(root, "modelPath");
                    runDirectory = ReadString(root, "runDirectory");
                    enginePath = ReadString(root, "enginePath");
                }
                if (eventName == "error") workerError = message;
                progress?.Report(new Yolo11TrainingProgress(eventName, ReadInt(root, "epoch"),
                    ReadInt(root, "totalEpochs"), ReadDouble(root, "trainLoss"), ReadDouble(root, "valLoss"), message));
            }
            catch (JsonException) { }
        }
        var error = await errorTask;
        await process.WaitForExitAsync(cancellationToken);
        if (workerError is not null) throw new InvalidOperationException(workerError);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"ATU5 训练进程退出，代码 {process.ExitCode}" : error.Trim());
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(runDirectory))
            throw new InvalidOperationException("ATU5 训练未返回模型保存路径。");
        TryDeletePauseFile();
        _pauseFilePath = null;
        _paused = false;
        return new Yolo11TrainingResult(modelPath, runDirectory, enginePath);
    }

    public Task CancelAsync()
    {
        TryDeletePauseFile();
        if (_process is { } process)
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

    public void Dispose()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        TryDeletePauseFile();
        ProcessPauseController.TryResume(process);
        PythonProcessSupport.TryKill(process);
        _paused = false;
        process.Dispose();
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

    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static int ReadInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double? ReadDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    private bool TryGetRunningProcess(out Process? process)
    {
        process = _process;
        if (process is null) return false;
        try
        {
            if (!process.HasExited) return true;
        }
        catch (InvalidOperationException)
        {
            // A completed/disposed Process may no longer have an associated OS process.
        }

        if (ReferenceEquals(_process, process)) _process = null;
        process = null;
        return false;
    }
}
