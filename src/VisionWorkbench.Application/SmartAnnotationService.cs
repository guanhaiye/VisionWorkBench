using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record YoloEPrompt(string ClassName, int ClassId, double X, double Y, double Width, double Height);
public sealed record Sam1Prompt(double X, double Y, int Label);

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
public sealed class SmartAnnotationService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _yoloeScript;
    private readonly string _sam1Script;
    private readonly string _yoloePython;
    private readonly string _sam1Python;
    private readonly SemaphoreSlim _yoloeGate = new(1, 1);
    private readonly SemaphoreSlim _sam1Gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;
    private Process? _yoloeProcess;
    private StreamWriter? _yoloeWriter;
    private StreamReader? _yoloeReader;
    private Process? _sam1Process;
    private StreamWriter? _sam1Writer;
    private StreamReader? _sam1Reader;

    public SmartAnnotationService(string workersRoot, string? configuredPython, string? yoloeModelPath, string? sam1ModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workersRoot);
        var root = Path.GetFullPath(workersRoot);
        var yoloeDirectory = Path.Combine(root, "yoloe");
        var sam1Directory = Path.Combine(root, "sam1");
        _yoloeScript = Path.Combine(yoloeDirectory, "worker.py");
        _sam1Script = Path.Combine(sam1Directory, "worker.py");
        _yoloePython = PythonProcessSupport.ResolvePython(configuredPython, yoloeDirectory,
            Path.Combine(root, "yolo11"), root);
        _sam1Python = PythonProcessSupport.ResolvePython(configuredPython, sam1Directory,
            Path.Combine(root, "yolo11"), root);
        YoloeModelPath = ResolveModelPath(
            yoloeModelPath,
            Path.Combine(yoloeDirectory, "models", "yoloe-11s-seg.pt"));
        Sam1ModelPath = ResolveModelPath(
            sam1ModelPath,
            Path.Combine(sam1Directory, "models", "sam_vit_b_01ec64.pth"));
    }

    public string YoloeModelPath { get; set; }
    public string Sam1ModelPath { get; set; }

    public string DescribeYoloeAvailability() => Describe(_yoloeScript, _yoloePython, YoloeModelPath, "YOLOE");
    public string DescribeSam1Availability() => Describe(_sam1Script, _sam1Python, Sam1ModelPath, "SAM1");

    public async Task WarmupSam1Async(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _sam1Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var response = await RunPersistentSam1Async(new { type = "warmup", modelPath = Sam1ModelPath }, timeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error)) throw new InvalidOperationException(response.Error);
            if (!response.Ready) throw new InvalidOperationException("SAM1 启动预热没有返回 ready 状态。");
        }
        finally { _sam1Gate.Release(); }
    }

    public async Task WarmupYoloEAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _yoloeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var warmupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            warmupTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var response = await RunPersistentYoloEAsync(new YoloEWarmupRequest
            {
                Type = "warmup",
                ModelPath = YoloeModelPath,
            }, warmupTimeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error))
                throw new InvalidOperationException(response.Error);
            if (!response.Ready)
                throw new InvalidOperationException("YOLOE 启动预热没有返回 ready 状态。");
        }
        finally
        {
            _yoloeGate.Release();
        }
    }

    public async Task<IReadOnlyList<SmartAnnotationImageResult>> RunYoloEAsync(
        string referenceImage,
        IReadOnlyList<YoloEPrompt> prompts,
        IReadOnlyList<string> targetImages,
        double confidence = 0.25,
        string outputMode = "both",
        CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _yoloeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await RunPersistentYoloEAsync(new YoloERequest
            {
                ModelPath = YoloeModelPath,
                ReferenceImage = referenceImage,
                Prompts = prompts,
                Targets = targetImages,
                Confidence = confidence,
                OutputMode = outputMode,
            }, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error))
                throw new InvalidOperationException(response.Error);
            return response.Results ?? [];
        }
        finally
        {
            _yoloeGate.Release();
        }
    }

    public async Task<SmartAnnotationObjectResult?> RunSam1ClickAsync(
        string imagePath,
        IReadOnlyList<Sam1Prompt> points,
        string className,
        CancellationToken cancellationToken = default)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _sam1Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await RunPersistentSam1Async(
                new Sam1Request
                {
                    ModelPath = Sam1ModelPath,
                    Image = imagePath,
                    ClassName = className,
                    Points = points.Select(point => new Sam1Point
                    {
                        X = point.X,
                        Y = point.Y,
                        Label = point.Label,
                    }).ToArray(),
                }, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error))
                throw new InvalidOperationException(response.Error);
            return response.Results?.SelectMany(x => x.Objects).FirstOrDefault();
        }
        finally { _sam1Gate.Release(); }
    }

    private async Task<SmartAnnotationResponse> RunPersistentSam1Async(object request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSam1Process();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        string? line;
        try
        {
            await _sam1Writer!.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
            await _sam1Writer.FlushAsync(timeout.Token).ConfigureAwait(false);
            line = await _sam1Reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            StopSam1Process();
            throw;
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            StopSam1Process();
            throw new InvalidOperationException("SAM1 常驻 Worker 意外退出。");
        }
        try
        {
            return JsonSerializer.Deserialize<SmartAnnotationResponse>(line, JsonOptions)
                ?? throw new InvalidOperationException("SAM1 返回为空。");
        }
        catch (JsonException ex)
        {
            StopSam1Process();
            throw new InvalidOperationException($"SAM1 返回格式无效：{line}", ex);
        }
    }

    private void EnsureSam1Process()
    {
        if (_sam1Process is { HasExited: false }) return;
        if (!File.Exists(_sam1Script)) throw new InvalidOperationException($"SAM1 适配器不存在：{_sam1Script}");
        StopSam1Process();
        var info = new ProcessStartInfo
        {
            FileName = _sam1Python,
            WorkingDirectory = Path.GetDirectoryName(_sam1Script)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(_sam1Script);
        info.ArgumentList.Add("--persistent");
        _sam1Process = new Process { StartInfo = info };
        if (!_sam1Process.Start()) throw new InvalidOperationException("无法启动 SAM1 常驻 Worker。");
        _sam1Writer = _sam1Process.StandardInput;
        _sam1Reader = _sam1Process.StandardOutput;
        _ = DrainErrorAsync(_sam1Process.StandardError);
    }

    private void StopSam1Process()
    {
        try { if (_sam1Process is { HasExited: false }) _sam1Process.Kill(true); } catch { }
        _sam1Writer?.Dispose();
        _sam1Reader?.Dispose();
        _sam1Process?.Dispose();
        _sam1Writer = null;
        _sam1Reader = null;
        _sam1Process = null;
    }

    private static async Task DrainErrorAsync(StreamReader reader)
    {
        // Persistent workers can log indefinitely; consume stderr without retaining its history.
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Stopping/restarting the worker closes its redirected pipe.
        }
    }

    private async Task<SmartAnnotationResponse> RunPersistentYoloEAsync(
        object request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureYoloEProcess();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        string? line;
        try
        {
            await _yoloeWriter!.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
            await _yoloeWriter.FlushAsync(timeout.Token).ConfigureAwait(false);
            do
            {
                line = await _yoloeReader!.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            } while (line is not null && !line.TrimStart().StartsWith('{'));
        }
        catch
        {
            StopYoloEProcess();
            throw;
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            StopYoloEProcess();
            throw new InvalidOperationException("YOLOE 常驻 Worker 意外退出。");
        }
        try
        {
            return JsonSerializer.Deserialize<SmartAnnotationResponse>(line, JsonOptions)
                ?? throw new InvalidOperationException("YOLOE 常驻 Worker 返回为空。");
        }
        catch (JsonException ex)
        {
            StopYoloEProcess();
            throw new InvalidOperationException($"YOLOE 常驻 Worker 返回格式无效：{line}", ex);
        }
    }

    private void EnsureYoloEProcess()
    {
        if (_yoloeProcess is { HasExited: false }) return;
        if (!File.Exists(_yoloeScript))
            throw new InvalidOperationException($"YOLOE 适配器不存在：{_yoloeScript}");
        StopYoloEProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = _yoloePython,
            WorkingDirectory = Path.GetDirectoryName(_yoloeScript)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(_yoloeScript);
        startInfo.ArgumentList.Add("--persistent");
        _yoloeProcess = new Process { StartInfo = startInfo };
        if (!_yoloeProcess.Start()) throw new InvalidOperationException("无法启动 YOLOE 常驻 Worker。");
        _yoloeWriter = _yoloeProcess.StandardInput;
        _yoloeReader = _yoloeProcess.StandardOutput;
        _ = DrainErrorAsync(_yoloeProcess.StandardError);
    }

    private void StopYoloEProcess()
    {
        try { if (_yoloeProcess is { HasExited: false }) _yoloeProcess.Kill(true); } catch { }
        _yoloeWriter?.Dispose();
        _yoloeReader?.Dispose();
        _yoloeProcess?.Dispose();
        _yoloeWriter = null;
        _yoloeReader = null;
        _yoloeProcess = null;
    }

    private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Cancel owners and queued calls before waiting; library continuations never need the UI thread.
        _shutdown.Cancel();
        _yoloeGate.Wait();
        _sam1Gate.Wait();
        try { StopYoloEProcess(); StopSam1Process(); }
        finally
        {
            _sam1Gate.Release();
            _yoloeGate.Release();
            _sam1Gate.Dispose();
            _yoloeGate.Dispose();
            _shutdown.Dispose();
        }
    }

    private static string Describe(string script, string python, string model, string name)
    {
        var missing = new List<string>();
        if (!File.Exists(script)) missing.Add($"适配器 {script}");
        if (!File.Exists(model))
        {
            if (name.Equals("YOLOE", StringComparison.OrdinalIgnoreCase))
                return $"YOLOE 将在首次推理时自动下载默认权重：{model}。Python：{python}";
            return $"SAM1 本地模型不存在：{model}。Python：{python}";
        }
        if (missing.Count == 0) return $"{name} 已配置：{model}";
        return $"{name} 未就绪：{string.Join("；", missing)}。Python：{python}";
    }

    private static string ResolveModelPath(string? configured, string defaultPath) =>
        !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? Path.GetFullPath(configured)
            : defaultPath;

    private sealed class YoloERequest
    {
        public string ModelPath { get; set; } = "";
        public string ReferenceImage { get; set; } = "";
        public IReadOnlyList<YoloEPrompt> Prompts { get; set; } = [];
        public IReadOnlyList<string> Targets { get; set; } = [];
        public double Confidence { get; set; } = 0.25;
        public string OutputMode { get; set; } = "both";
    }

    private sealed class YoloEWarmupRequest
    {
        public string Type { get; set; } = "warmup";
        public string ModelPath { get; set; } = "";
    }

    private sealed class Sam1Request
    {
        public string ModelPath { get; set; } = "";
        public string Image { get; set; } = "";
        public string ClassName { get; set; } = "object";
        public IReadOnlyList<Sam1Point> Points { get; set; } = [];
    }

    private sealed class Sam1Point
    {
        public double X { get; set; }
        public double Y { get; set; }
        public int Label { get; set; }
    }

    private sealed class SmartAnnotationResponse
    {
        public List<SmartAnnotationImageResult>? Results { get; set; }
        public string? Error { get; set; }
        public bool Ready { get; set; }
    }
}
