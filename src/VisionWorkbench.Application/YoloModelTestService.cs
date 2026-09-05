using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record YoloTestPoint(double X, double Y);
public sealed record YoloTestDetection(int ClassId, string ClassName, double Confidence,
    double X, double Y, double Width, double Height);
public sealed record YoloTestMask(int ClassId, string ClassName, double Confidence,
    IReadOnlyList<YoloTestPoint> Polygon);
public sealed record YoloModelTestResult(string Task, double ElapsedMs,
    IReadOnlyList<YoloTestDetection> Detections, IReadOnlyList<YoloTestMask> Masks);
public sealed record YoloBatchTestItem(string ImagePath, YoloModelTestResult? Result, string? Error);

public sealed class YoloModelTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _script;
    private readonly string _exportScript;
    private readonly string _batchScript;
    private readonly string _python;

    public YoloModelTestService(string workersRoot, string? configuredPython)
    {
        var directory = Path.Combine(workersRoot, "yolo11");
        _script = Path.Combine(directory, "test_worker.py");
        _exportScript = Path.Combine(directory, "export_worker.py");
        _batchScript = Path.Combine(directory, "batch_test_worker.py");
        _python = PythonProcessSupport.ResolvePython(configuredPython, directory, workersRoot);
    }

    public async Task<IReadOnlyList<YoloBatchTestItem>> RunBatchAsync(string modelPath,
        IReadOnlyList<string> imagePaths, double confidence, double iou, string device,
        IProgress<YoloBatchTestItem>? progress = null, CancellationToken cancellationToken = default)
    {
        modelPath = ResolveInferenceModelPath(modelPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = _python, WorkingDirectory = Path.GetDirectoryName(_batchScript)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(_batchScript);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { modelPath, imagePaths, confidence, iou, device }, JsonOptions));
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        var errorsTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var items = new List<YoloBatchTestItem>();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            Response? response;
            try { response = JsonSerializer.Deserialize<Response>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (response?.Event != "result")
            {
                if (response?.Event == "error") throw new InvalidOperationException(response.Error);
                continue;
            }
            var item = new YoloBatchTestItem(response.ImagePath ?? "", response.Success
                ? new YoloModelTestResult(response.Task ?? "unknown", response.ElapsedMs,
                    response.Detections ?? [], response.Masks ?? []) : null, response.Error);
            items.Add(item);
            progress?.Report(item);
        }
        await process.WaitForExitAsync(cancellationToken);
        var errors = await errorsTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? "批量测试进程失败。" : errors.Trim());
        return items;
    }

    public async Task<YoloModelTestResult> RunAsync(string modelPath, string imagePath,
        double confidence, double iou, string device, CancellationToken cancellationToken = default)
    {
        modelPath = ResolveInferenceModelPath(modelPath);
        if (!File.Exists(_script)) throw new FileNotFoundException("模型测试 Worker 不存在。", _script);
        var startInfo = new ProcessStartInfo
        {
            FileName = _python,
            WorkingDirectory = Path.GetDirectoryName(_script)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(_script);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var payload = JsonSerializer.Serialize(new { modelPath, imagePath, confidence, iou, device }, JsonOptions);
        await process.StandardInput.WriteAsync(payload);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        var json = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith('{'));
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"测试进程没有返回有效结果。{error}".Trim());
        }
        Response? response;
        try
        {
            response = JsonSerializer.Deserialize<Response>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"无法解析测试结果：{exception.Message}", exception);
        }
        if (response is null)
        {
            throw new InvalidOperationException("测试进程返回为空。");
        }
        if (!response.Success) throw new InvalidOperationException(response.Error ?? error);
        return new YoloModelTestResult(response.Task ?? "unknown", response.ElapsedMs,
            response.Detections ?? [], response.Masks ?? []);
    }

    /// <summary>Engine 优先：显式选择 engine 时直接使用；选择 pt 时优先使用同名 engine。</summary>
    public static string ResolveInferenceModelPath(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return selectedPath;
        var fullPath = Path.GetFullPath(selectedPath);
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".engine", StringComparison.OrdinalIgnoreCase)) return fullPath;
        if (extension.Equals(".pt", StringComparison.OrdinalIgnoreCase))
        {
            var enginePath = Path.ChangeExtension(fullPath, ".engine");
            if (File.Exists(enginePath)) return enginePath;
        }
        return fullPath;
    }

    public async Task<string> ExportTensorRtAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_exportScript)) throw new FileNotFoundException("TensorRT 导出 Worker 不存在。", _exportScript);
        var startInfo = new ProcessStartInfo
        {
            FileName = _python,
            WorkingDirectory = Path.GetDirectoryName(_exportScript)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(_exportScript);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { modelPath }, JsonOptions));
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        var json = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith('{'));
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException($"TensorRT 导出没有返回结果。{error}".Trim());
        using var response = JsonDocument.Parse(json);
        var root = response.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var message = root.TryGetProperty("error", out var errorProperty) ? errorProperty.GetString() : error;
            throw new InvalidOperationException(message ?? "TensorRT 导出失败。");
        }
        var enginePath = root.GetProperty("enginePath").GetString();
        if (string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath))
            throw new InvalidOperationException("TensorRT 导出完成，但没有找到 Engine 文件。");
        return enginePath;
    }

    private sealed class Response
    {
        public string? Event { get; set; }
        public string? ImagePath { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? Task { get; set; }
        public double ElapsedMs { get; set; }
        public List<YoloTestDetection>? Detections { get; set; }
        public List<YoloTestMask>? Masks { get; set; }
    }
}
