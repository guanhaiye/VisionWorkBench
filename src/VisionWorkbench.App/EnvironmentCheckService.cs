using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.App;

/// <summary>安装完成后和客户报障时使用的轻量环境自检。</summary>
internal static class EnvironmentCheckService
{
    private sealed record CheckResult(string Name, bool Ok, string Detail);
    private static CheckResult? _lastCuda;

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        _lastCuda = null;
        var settings = AppServices.Instance.Settings;
        var logDirectory = Path.Combine(settings.DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "environment-check.log");
        var results = new List<CheckResult>
        {
            CheckWritableDirectory(settings.DataDirectory, "参数数据目录"),
            CheckWritableDirectory(settings.ConfigDirectory, "配置目录"),
            CheckFile(Path.Combine(AppContext.BaseDirectory, "plugins", "yolo11", "worker.py"), "YOLO Worker"),
            CheckFile(Path.Combine(AppContext.BaseDirectory, "runtime", "mvs", "Win64_x64", "MvCameraControl.dll"), "Hikvision MVS Runtime"),
            CheckFile(Path.Combine(AppContext.BaseDirectory, "plugins", "yolo11", "models", "yolo11n.pt"), "YOLO基础模型"),
        };
        if (!HasBundledModels())
        {
            results[4] = new CheckResult("Model manifest", true, "No unlicensed model bundled; import an authorized model after installation.");
        }

        var python = ResolvePython();
        if (python is null)
        {
            results.Add(new CheckResult("Python运行时", false, "未找到安装目录内置的 runtime\\python\\python.exe"));
        }
        else
        {
            results.Add(await CheckPythonAsync(python, cancellationToken));
        }
        if (_lastCuda is not null) results.Add(_lastCuda);

        var cuda = results.FirstOrDefault(item => item.Name == "Torch CUDA");
        var device = cuda?.Ok == true ? "NVIDIA GPU" : "CPU";
        var builder = new StringBuilder();
        builder.AppendLine($"VisionWorkbench environment check - {DateTimeOffset.Now:O}");
        builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        builder.AppendLine($"DataDirectory: {settings.DataDirectory}");
        builder.AppendLine($"Python: {python ?? "<missing>"}");
        builder.AppendLine($"SelectedDevice: {device}");
        foreach (var result in results)
        {
            builder.AppendLine($"[{(result.Ok ? "PASS" : "FAIL")}] {result.Name}: {result.Detail}");
        }
        File.WriteAllText(logPath, builder.ToString(), new UTF8Encoding(false));

        var requiredFailures = results.Where(item => !item.Ok && item.Name is not "Torch CUDA").ToArray();
        Console.WriteLine(requiredFailures.Length == 0
            ? $"环境检查通过；当前运行设备：{device}；日志：{logPath}"
            : $"环境检查失败；请查看日志：{logPath}");
        return requiredFailures.Length == 0 ? 0 : 1;
    }

    private static CheckResult CheckWritableDirectory(string path, string name)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".environment-check-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new CheckResult(name, true, path);
        }
        catch (Exception ex)
        {
            return new CheckResult(name, false, ex.Message);
        }
    }

    private static CheckResult CheckFile(string path, string name) =>
        new(name, File.Exists(path), File.Exists(path) ? path : $"文件不存在：{path}");

    private static bool HasBundledModels()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "model-manifest.json");
        if (!File.Exists(manifestPath)) return true;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("models", out var models)
                && models.EnumerateArray().Any(model => model.TryGetProperty("included", out var flag) && flag.GetBoolean());
        }
        catch
        {
            return true;
        }
    }

    private static string? ResolvePython()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe"),
            AppServices.Instance.Settings.PythonExecutable,
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static async Task<CheckResult> CheckPythonAsync(string python, CancellationToken cancellationToken)
    {
        const string probe = "import json,sys; result={'python':sys.version.split()[0]}; "
            + "import cv2; result['cv2']=cv2.__version__; "
            + "import ultralytics; result['ultralytics']=ultralytics.__version__; "
            + "import torch; result['torch']=torch.__version__; result['cuda']=bool(torch.cuda.is_available()); "
            + "result['gpu']=torch.cuda.get_device_name(0) if result['cuda'] else ''; print(json.dumps(result,ensure_ascii=False))";
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = python,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                },
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(probe);
            if (!process.Start()) return new CheckResult("Python运行时", false, "无法启动 Python");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                return new CheckResult("Python运行时", false, string.IsNullOrWhiteSpace(error) ? $"退出码 {process.ExitCode}" : error.Trim());
            }
            using var json = JsonDocument.Parse(output.Trim());
            var root = json.RootElement;
            var torch = root.GetProperty("torch").GetString() ?? "unknown";
            var cuda = root.GetProperty("cuda").GetBoolean();
            var gpu = root.GetProperty("gpu").GetString();
            _lastCuda = new CheckResult("Torch CUDA", cuda, cuda ? gpu ?? "NVIDIA GPU" : "不可用，已回退 CPU");
            return new CheckResult("Python运行时", true,
                $"Python {root.GetProperty("python").GetString()}；Torch {torch}；OpenCV {root.GetProperty("cv2").GetString()}；Ultralytics {root.GetProperty("ultralytics").GetString()}");
        }
        catch (OperationCanceledException)
        {
            return new CheckResult("Python运行时", false, "检查超时");
        }
        catch (Exception ex)
        {
            return new CheckResult("Python运行时", false, ex.Message);
        }
    }
}
