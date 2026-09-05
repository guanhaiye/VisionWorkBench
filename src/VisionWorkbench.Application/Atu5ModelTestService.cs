using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

/// <summary>ATU5/FPN 语义分割 Python 推理 Worker。</summary>
public sealed class Atu5ModelTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _scriptPath;
    private readonly string _batchScriptPath;
    private readonly string _python;

    public Atu5ModelTestService(string workersRoot, string? configuredPython)
    {
        var directory = Path.Combine(workersRoot, "atu5");
        _scriptPath = Path.Combine(directory, "test_worker.py");
        _batchScriptPath = Path.Combine(directory, "batch_test_worker.py");
        _python = PythonProcessSupport.ResolvePython(configuredPython, directory,
            Path.Combine(workersRoot, "yolo11"), workersRoot);
    }

    public async Task<IReadOnlyList<YoloBatchTestItem>> RunBatchAsync(
        string modelPath, IReadOnlyList<string> imagePaths, string device,
        IProgress<YoloBatchTestItem>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_batchScriptPath)) throw new FileNotFoundException("ATU5 批量推理 Worker 不存在。", _batchScriptPath);
        var results = new List<YoloBatchTestItem>();
        var info = new ProcessStartInfo
        {
            FileName = _python, WorkingDirectory = Path.GetDirectoryName(_batchScriptPath)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        info.ArgumentList.Add(_batchScriptPath);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动 ATU5 批量推理 Worker。");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                PythonProcessSupport.TryKill(process);
            }
            catch { }
        });
        var payload = JsonSerializer.Serialize(new { modelPath, imagePaths, device }, JsonOptions);
        await process.StandardInput.WriteAsync(payload);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        string? workerError = null;
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventName = root.TryGetProperty("event", out var eventProperty)
                ? eventProperty.GetString() ?? "log" : "log";
            if (eventName == "error")
            {
                workerError = root.TryGetProperty("error", out var errorProperty) ? errorProperty.GetString() : "ATU5 批量推理失败";
                continue;
            }
            if (eventName != "result") continue;
            var imagePath = root.TryGetProperty("imagePath", out var imageProperty) ? imageProperty.GetString() ?? "" : "";
            var success = root.TryGetProperty("success", out var successProperty) && successProperty.GetBoolean();
            YoloModelTestResult? result = null;
            string? error = root.TryGetProperty("error", out var resultErrorProperty) ? resultErrorProperty.GetString() : null;
            if (success)
            {
                var detections = root.TryGetProperty("detections", out var detectionProperty)
                    ? JsonSerializer.Deserialize<List<YoloTestDetection>>(detectionProperty.GetRawText(), JsonOptions) ?? [] : [];
                var masks = root.TryGetProperty("masks", out var maskProperty)
                    ? JsonSerializer.Deserialize<List<YoloTestMask>>(maskProperty.GetRawText(), JsonOptions) ?? [] : [];
                var elapsed = root.TryGetProperty("elapsedMs", out var elapsedProperty) ? elapsedProperty.GetDouble() : 0;
                result = new YoloModelTestResult("semantic", elapsed, detections, masks);
            }
            var item = new YoloBatchTestItem(imagePath, result, error);
            results.Add(item);
            progress?.Report(item);
        }
        await process.WaitForExitAsync(cancellationToken);
        var errorOutput = await errorTask;
        if (workerError is not null) throw new InvalidOperationException(workerError);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput) ? "ATU5 批量推理进程失败。" : errorOutput.Trim());
        return results;
    }

    private async Task<YoloModelTestResult> RunAsync(
        string modelPath, string imagePath, string device, CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath)) throw new FileNotFoundException("ATU5 推理 Worker 不存在。", _scriptPath);
        var info = new ProcessStartInfo
        {
            FileName = _python, WorkingDirectory = Path.GetDirectoryName(_scriptPath)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        info.ArgumentList.Add(_scriptPath);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动 ATU5 推理 Worker。");
        var payload = JsonSerializer.Serialize(new { modelPath, imagePath, device }, JsonOptions);
        await process.StandardInput.WriteAsync(payload);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith('{'));
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "ATU5 推理没有返回有效结果。" : error.Trim());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            throw new InvalidOperationException(root.TryGetProperty("error", out var errorProperty) ? errorProperty.GetString() : "ATU5 推理失败");
        var detections = root.TryGetProperty("detections", out var d) ? JsonSerializer.Deserialize<List<YoloTestDetection>>(d.GetRawText(), JsonOptions) ?? [] : [];
        var masks = root.TryGetProperty("masks", out var m) ? JsonSerializer.Deserialize<List<YoloTestMask>>(m.GetRawText(), JsonOptions) ?? [] : [];
        return new YoloModelTestResult("semantic", root.TryGetProperty("elapsedMs", out var elapsed) ? elapsed.GetDouble() : 0, detections, masks);
    }

}
