using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record YoloEPrompt(string ClassName, int ClassId, double X, double Y, double Width, double Height);

public sealed class SmartAnnotationObjectResult
{
    public string ClassName { get; set; } = "";
    public string Shape { get; set; } = "bbox";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<DatasetPoint> Polygon { get; set; } = [];
    public double Score { get; set; }
}

public sealed class SmartAnnotationImageResult
{
    public string ImagePath { get; set; } = "";
    public List<SmartAnnotationObjectResult> Objects { get; set; } = [];
}

/// <summary>
/// 智能标注适配层。C# 负责生命周期、超时和错误呈现，模型运行由 workers 下的隔离 Python 适配器完成。
/// </summary>
public sealed class SmartAnnotationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _yoloeScript;
    private readonly string _sam3Script;
    private readonly string _yoloePython;
    private readonly string _sam3Python;

    public SmartAnnotationService(string workersRoot, string? configuredPython, string? yoloeModelPath, string? sam3ModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workersRoot);
        var root = Path.GetFullPath(workersRoot);
        var yoloeDirectory = Path.Combine(root, "yoloe");
        var sam3Directory = Path.Combine(root, "sam3");
        _yoloeScript = Path.Combine(yoloeDirectory, "worker.py");
        _sam3Script = Path.Combine(sam3Directory, "worker.py");
        _yoloePython = ResolvePython(configuredPython, yoloeDirectory, Path.Combine(root, "yolo11"), root);
        _sam3Python = ResolvePython(configuredPython, sam3Directory, Path.Combine(root, "yolo11"), root);
        YoloeModelPath = string.IsNullOrWhiteSpace(yoloeModelPath)
            ? Path.Combine(yoloeDirectory, "models", "yoloe-11s-seg.pt")
            : Path.GetFullPath(yoloeModelPath);
        Sam3ModelPath = string.IsNullOrWhiteSpace(sam3ModelPath)
            ? Path.Combine(sam3Directory, "models", "sam3.pt")
            : Path.GetFullPath(sam3ModelPath);
    }

    public string YoloeModelPath { get; set; }
    public string Sam3ModelPath { get; set; }

    public string DescribeYoloeAvailability() => Describe(_yoloeScript, _yoloePython, YoloeModelPath, "YOLOE");
    public string DescribeSam3Availability() => Describe(_sam3Script, _sam3Python, Sam3ModelPath, "SAM3");

    public Task<IReadOnlyList<SmartAnnotationImageResult>> RunYoloEAsync(
        string referenceImage,
        IReadOnlyList<YoloEPrompt> prompts,
        IReadOnlyList<string> targetImages,
        double confidence = 0.25,
        CancellationToken cancellationToken = default) =>
        RunAsync<YoloERequest, SmartAnnotationResponse>(
            _yoloePython,
            _yoloeScript,
            new YoloERequest
            {
                ModelPath = YoloeModelPath,
                ReferenceImage = referenceImage,
                Prompts = prompts,
                Targets = targetImages,
                Confidence = confidence,
            },
            cancellationToken).ContinueWith(task =>
            {
                if (task.IsCanceled) throw new TaskCanceledException(task);
                if (task.IsFaulted) throw task.Exception?.GetBaseException() ?? new InvalidOperationException("YOLOE 执行失败");
                return (IReadOnlyList<SmartAnnotationImageResult>)(task.Result.Results ?? []);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async Task<SmartAnnotationObjectResult?> RunSam3ClickAsync(
        string imagePath,
        DatasetPoint point,
        string className,
        CancellationToken cancellationToken = default)
    {
        var response = await RunAsync<Sam3Request, SmartAnnotationResponse>(
            _sam3Python,
            _sam3Script,
            new Sam3Request
            {
                ModelPath = Sam3ModelPath,
                Image = imagePath,
                ClassName = className,
                Points = [new Sam3Point { X = point.X, Y = point.Y, Label = 1 }],
            },
            cancellationToken);
        return response.Results?.SelectMany(x => x.Objects).FirstOrDefault();
    }

    private static async Task<TResponse> RunAsync<TRequest, TResponse>(
        string python,
        string script,
        TRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(script))
            throw new InvalidOperationException($"智能标注适配器不存在：{script}");

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(script);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Python 智能标注进程");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"无法启动 Python：{python}。请检查模型对应的虚拟环境。", ex);
        }

        await using (var writer = process.StandardInput)
        {
            await writer.WriteAsync(JsonSerializer.Serialize(request, JsonOptions));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("智能标注超过 15 分钟未完成，已终止任务。");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"智能标注进程失败（退出码 {process.ExitCode}）：{detail.Trim()}");
        }

        var json = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith('{'));
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"智能标注没有返回 JSON。{stderr.Trim()}");
        try
        {
            var response = JsonSerializer.Deserialize<TResponse>(json, JsonOptions);
            return response ?? throw new InvalidOperationException("智能标注返回为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"智能标注返回格式无效：{json}", ex);
        }
    }

    private static string ResolvePython(string? configured, string primary, string fallback, string root)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var candidates = new[]
        {
            Path.Combine(primary, ".venv", "Scripts", "python.exe"),
            Path.Combine(fallback, ".venv", "Scripts", "python.exe"),
            Path.Combine(root, ".venv", "Scripts", "python.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? "python";
    }

    private static string Describe(string script, string python, string model, string name)
    {
        var missing = new List<string>();
        if (!File.Exists(script)) missing.Add($"适配器 {script}");
        if (!File.Exists(model)) missing.Add($"模型 {model}");
        if (missing.Count == 0) return $"{name} 已配置：{model}";
        return $"{name} 未就绪：{string.Join("；", missing)}。Python：{python}";
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
    }

    private sealed class YoloERequest
    {
        public string ModelPath { get; set; } = "";
        public string ReferenceImage { get; set; } = "";
        public IReadOnlyList<YoloEPrompt> Prompts { get; set; } = [];
        public IReadOnlyList<string> Targets { get; set; } = [];
        public double Confidence { get; set; } = 0.25;
    }

    private sealed class Sam3Request
    {
        public string ModelPath { get; set; } = "";
        public string Image { get; set; } = "";
        public string ClassName { get; set; } = "object";
        public IReadOnlyList<Sam3Point> Points { get; set; } = [];
    }

    private sealed class Sam3Point
    {
        public double X { get; set; }
        public double Y { get; set; }
        public int Label { get; set; }
    }

    private sealed class SmartAnnotationResponse
    {
        public List<SmartAnnotationImageResult>? Results { get; set; }
    }
}
