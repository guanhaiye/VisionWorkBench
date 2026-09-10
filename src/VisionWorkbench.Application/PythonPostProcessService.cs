using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;

namespace VisionWorkbench.Application;

/// <summary>
/// 运行任务配置中的 Python 后处理脚本。脚本固定使用 input_data 作为输入、
/// result 作为输出；任务类型不同，宿主会把结果转换为统一的 items 集合。
/// </summary>
public sealed class PythonPostProcessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _configuredPython;
    private readonly ILogger? _logger;

    public PythonPostProcessService(string? configuredPython, ILogger? logger = null)
    {
        _configuredPython = configuredPython;
        _logger = logger;
    }

    public async Task<PythonPostProcessResult> ExecuteAsync(
        string script,
        string imagePath,
        AlgorithmOutput output,
        InspectionTaskType taskType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"visionworkbench-postprocess-{Guid.NewGuid():N}.py");
        try
        {
            // 宿主固定提供 input_data，并要求用户把最终结果赋给 result。
            // 用户脚本中的内部变量、import 和处理逻辑不受限制。
            var runtimeScript = string.Join(Environment.NewLine,
            [
                "import json",
                "import sys",
                "",
                "input_data = json.load(sys.stdin)",
                "result = None",
                "",
                "# ---- 用户脚本开始：固定输入 input_data，固定输出 result ----",
                script,
                "# ---- 用户脚本结束 ----",
                "",
                "if not isinstance(result, dict):",
                "    raise TypeError(\"脚本必须给固定输出变量 result 赋值为 JSON 对象\")",
                "print(json.dumps(result, ensure_ascii=False))",
            ]);
            await File.WriteAllTextAsync(scriptPath, runtimeScript, new UTF8Encoding(false), cancellationToken);
            var input = new
            {
                schemaVersion = "1.0",
                taskType = taskType.Normalize().ToString(),
                imagePath,
                imageWidth = output.ImageWidth,
                imageHeight = output.ImageHeight,
                items = BuildItems(taskType, output),
                raw = new
                {
                    output,
                    detections = output.Detections,
                    segmentations = output.Segmentations,
                    classifications = output.Classifications,
                    metrics = output.Metrics,
                    events = output.Events,
                    tracks = output.Tracks,
                },
            };

            var python = ResolvePythonExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Python 解释器。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, JsonOptions));
            await process.StandardInput.WriteAsync("\n");
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            if (completed != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已经退出 */ }
                throw new TimeoutException("Python 后处理脚本执行超过 10 秒，已终止。");
            }
            await waitTask;
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(FormatProcessError(stderr, process.ExitCode));
            }

            using var document = JsonDocument.Parse(ExtractJson(stdout));
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Python 脚本必须输出 JSON 对象。");
            }
            ValidateResultSchema(result);

            var commonIndices = TryReadIndices(result, "keepIndices", out var indices) ? indices : null;
            var filteredOutput = ApplyPrimaryFilter(taskType, output, commonIndices);
            var pythonEvents = ReadEvents(result);
            if (pythonEvents.Count > 0)
            {
                filteredOutput = filteredOutput with
                {
                    Events = [.. filteredOutput.Events, .. pythonEvents],
                };
            }

            var explicitStatus = result.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            var status = explicitStatus?.Trim().ToLowerInvariant() switch
            {
                "ok" or "pass" or "passed" => DecisionStatus.Ok,
                "ng" or "fail" or "failed" => DecisionStatus.Ng,
                _ => HasObjects(filteredOutput) ? DecisionStatus.Ok : DecisionStatus.Ng,
            };
            var message = result.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : $"Python 后处理完成，保留 {filteredOutput.Detections.Count + filteredOutput.Segmentations.Count} 个对象";
            var decision = new DecisionResult
            {
                Status = status,
                Outcomes =
                [
                    new RuleOutcome
                    {
                        RuleId = "python-post-process",
                        RuleKind = nameof(PostProcessMode.PythonScript),
                        Passed = status == DecisionStatus.Ok,
                        Message = message,
                    },
                ],
            };
            return new PythonPostProcessResult(filteredOutput with { Decision = decision }, decision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Python 后处理脚本执行失败");
            var decision = new DecisionResult
            {
                Status = DecisionStatus.Ng,
                Outcomes =
                [
                    new RuleOutcome
                    {
                        RuleId = "python-post-process",
                        RuleKind = nameof(PostProcessMode.PythonScript),
                        Passed = false,
                        Message = $"Python 后处理失败：{ex.Message}",
                    },
                ],
            };
            return new PythonPostProcessResult(output with { Decision = decision }, decision);
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { /* 清理失败不影响检测结果 */ }
        }
    }

    private string ResolvePythonExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_configuredPython))
        {
            return _configuredPython.Trim();
        }

        var roots = new List<DirectoryInfo>();
        foreach (var start in new[]
                 { new DirectoryInfo(Environment.CurrentDirectory), new DirectoryInfo(AppContext.BaseDirectory) })
        {
            var directory = start;
            for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
            {
                roots.Add(directory);
            }
        }
        var candidates = roots
            .SelectMany(root => new[]
            {
                Path.Combine(root.FullName, "workers", ".venv", "Scripts", "python.exe"),
                Path.Combine(root.FullName, ".venv", "Scripts", "python.exe"),
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(File.Exists) ?? "python";
    }

    private static string FormatProcessError(string stderr, int exitCode) =>
        string.IsNullOrWhiteSpace(stderr)
            ? $"Python 脚本退出码为 {exitCode}。"
            : $"Python 脚本退出码为 {exitCode}：{stderr.Trim()}";

    private static string ExtractJson(string stdout)
    {
        var text = stdout.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Python 脚本没有输出 JSON 结果。");

        // 允许脚本输出少量调试日志，但优先尝试完整输出，避免嵌套 JSON 被截断。
        try
        {
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException)
        {
            foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                var candidate = line.Trim();
                if (!candidate.StartsWith('{') || !candidate.EndsWith('}'))
                    continue;
                try
                {
                    using var _ = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException)
                {
                    // 继续从后往前查找最后一行 JSON。
                }
            }
        }
        throw new InvalidOperationException("Python 脚本没有输出有效的 JSON 结果。");
    }

    private static IReadOnlyList<object> BuildItems(InspectionTaskType taskType, AlgorithmOutput output)
    {
        return taskType.Normalize() switch
        {
            InspectionTaskType.SemanticSegmentation or InspectionTaskType.InstanceSegmentation
                => BuildSegmentationItems(output),
            InspectionTaskType.BehaviorRecognition
                => BuildBehaviorItems(output),
            _ when output.Detections.Count > 0
                => BuildDetectionItems(output),
            _ when output.Segmentations.Count > 0
                => BuildSegmentationItems(output),
            _ when output.Events.Count > 0
                => BuildBehaviorItems(output),
            _ => output.Classifications.Select((item, index) => (object)new
            {
                index,
                kind = "classification",
                classId = item.ClassId,
                className = item.ClassName,
                confidence = item.Confidence,
            }).ToArray(),
        };
    }

    private static IReadOnlyList<object> BuildDetectionItems(AlgorithmOutput output) =>
        output.Detections.Select((item, index) =>
        {
            var areaPx = item.AreaRatio > 0
                ? item.AreaRatio * output.ImageWidth * output.ImageHeight
                : item.Box.Width * item.Box.Height * output.ImageWidth * output.ImageHeight;
            var diameterPx = Math.Max(item.Box.Width * output.ImageWidth, item.Box.Height * output.ImageHeight);
            return (object)new
            {
                index,
                kind = "detection",
                classId = item.ClassId,
                className = item.ClassName,
                confidence = item.Confidence,
                areaPx,
                diameterPx,
                box = new
                {
                    x = item.Box.X * output.ImageWidth,
                    y = item.Box.Y * output.ImageHeight,
                    width = item.Box.Width * output.ImageWidth,
                    height = item.Box.Height * output.ImageHeight,
                },
                trackId = item.TrackId,
                severity = item.Severity,
            };
        }).ToArray();

    private static IReadOnlyList<object> BuildSegmentationItems(AlgorithmOutput output) =>
        output.Segmentations.Select((item, index) =>
        {
            var points = item.Contours.SelectMany(contour => contour).ToArray();
            var diameterPx = points.Length == 0
                ? 0
                : Math.Max(
                    (points.Max(point => point.X) - points.Min(point => point.X)) * output.ImageWidth,
                    (points.Max(point => point.Y) - points.Min(point => point.Y)) * output.ImageHeight);
            return (object)new
            {
                index,
                kind = "segmentation",
                mode = item.Mode,
                classId = item.ClassId,
                confidence = item.Confidence,
                areaPx = item.AreaRatio * output.ImageWidth * output.ImageHeight,
                diameterPx,
                contours = item.Contours,
                maskImagePath = item.MaskImagePath,
            };
        }).ToArray();

    private static IReadOnlyList<object> BuildBehaviorItems(AlgorithmOutput output) =>
        output.Events.Select((item, index) => (object)new
        {
            index,
            kind = "behaviorEvent",
            eventId = item.EventId,
            eventType = item.EventType,
            phase = item.Phase.ToString(),
            severity = item.Severity.ToString(),
            subjectId = item.SubjectId,
            regionId = item.RegionId,
            confidence = item.Confidence,
            startedAt = item.StartedAt,
            endedAt = item.EndedAt,
            durationMs = item.StartedAt is { } start && item.EndedAt is { } end
                ? (end - start).TotalMilliseconds
                : 0,
            message = item.Message,
        }).ToArray();

    private static AlgorithmOutput ApplyPrimaryFilter(
        InspectionTaskType taskType,
        AlgorithmOutput output,
        IReadOnlyList<int>? indices)
    {
        if (indices is null)
            return output;

        return taskType.Normalize() switch
        {
            InspectionTaskType.SemanticSegmentation or InspectionTaskType.InstanceSegmentation
                => output with { Segmentations = FilterByIndices(output.Segmentations, indices) },
            InspectionTaskType.BehaviorRecognition
                => output with { Events = FilterByIndices(output.Events, indices) },
            _ when output.Detections.Count > 0
                => output with { Detections = FilterByIndices(output.Detections, indices) },
            _ when output.Segmentations.Count > 0
                => output with { Segmentations = FilterByIndices(output.Segmentations, indices) },
            _ when output.Events.Count > 0
                => output with { Events = FilterByIndices(output.Events, indices) },
            _ => output,
        };
    }

    private static IReadOnlyList<T> Filter<T>(IReadOnlyList<T> source, JsonElement result, string propertyName)
    {
        return TryReadIndices(result, propertyName, out var indices)
            ? FilterByIndices(source, indices)
            : source;
    }

    private static void ValidateResultSchema(JsonElement result)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "keepIndices",
            "status",
            "message",
            "events",
        };
        var unknown = result.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !allowed.Contains(name))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"脚本输出包含未定义字段：{string.Join("、", unknown)}。请只使用固定输出字段。");
        }
        if (!result.TryGetProperty("status", out _))
        {
            throw new InvalidOperationException("脚本输出必须包含固定字段 result.status，值只能是 ok 或 ng。");
        }
        if (result.TryGetProperty("status", out var status)
            && (status.ValueKind != JsonValueKind.String
                || status.GetString()?.Trim().ToLowerInvariant() is not ("ok" or "ng")))
        {
            throw new InvalidOperationException("result.status 只能设置为 ok 或 ng。");
        }
        if (result.TryGetProperty("message", out var message)
            && message.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("result.message 必须是字符串。");
        }
    }

    private static bool TryReadIndices(JsonElement result, string propertyName, out int[] indices)
    {
        indices = [];
        if (!result.TryGetProperty(propertyName, out var element))
            return false;
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{propertyName} 必须是整数数组。");
        indices = element.EnumerateArray().Select(item =>
        {
            if (!item.TryGetInt32(out var value) || value < 0)
                throw new InvalidOperationException($"{propertyName} 中包含无效索引。");
            return value;
        }).Distinct().ToArray();
        return true;
    }

    private static IReadOnlyList<VisionEvent> ReadEvents(JsonElement result)
    {
        if (!result.TryGetProperty("events", out var element))
        {
            return [];
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("result.events 必须是数组。");
        }

        var events = new List<VisionEvent>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("result.events 中的每个事件都必须是对象。");

            var eventId = ReadRequiredString(item, "eventId");
            var eventType = ReadRequiredString(item, "eventType");
            var phase = ReadEnum(item, "phase", VisionEventPhase.Started);
            var severity = ReadEnum(item, "severity", VisionEventSeverity.Warning);

            events.Add(new VisionEvent
            {
                EventId = eventId,
                EventType = eventType,
                Phase = phase,
                Severity = severity,
                SubjectId = ReadString(item, "subjectId"),
                RegionId = ReadString(item, "regionId"),
                Confidence = ReadDouble(item, "confidence"),
                Message = ReadString(item, "message"),
                TextValue = ReadString(item, "textValue"),
                CodeValue = ReadString(item, "codeValue"),
                EvidenceImagePath = ReadString(item, "evidenceImagePath"),
                SourceStationCode = ReadString(item, "sourceStationCode"),
                Count = ReadNullableLong(item, "count"),
                FrameSequence = ReadNullableLong(item, "frameSequence"),
                StartedAt = ReadDateTimeOffset(item, "startedAt"),
                EndedAt = ReadDateTimeOffset(item, "endedAt"),
            });
        }
        return events;
    }

    private static string ReadRequiredString(JsonElement item, string propertyName)
    {
        var value = ReadString(item, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"result.events 中每个事件都必须包含 {propertyName}。");
        return value;
    }

    private static string? ReadString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static double ReadDouble(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDouble()
            : 0;

    private static long? ReadNullableLong(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt64()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDateTimeOffset()
            : null;

    private static TEnum ReadEnum<TEnum>(JsonElement item, string propertyName, TEnum fallback)
        where TEnum : struct, Enum
    {
        var text = ReadString(item, propertyName);
        return string.IsNullOrWhiteSpace(text)
            ? fallback
            : Enum.TryParse<TEnum>(text, ignoreCase: true, out var value)
                ? value
                : throw new InvalidOperationException($"result.events.{propertyName} 值无效：{text}。");
    }

    private static IReadOnlyList<T> FilterByIndices<T>(IReadOnlyList<T> source, IReadOnlyList<int> indices)
    {
        var filtered = new List<T>(indices.Count);
        foreach (var index in indices)
        {
            if (index >= source.Count)
                throw new InvalidOperationException($"Python 返回了越界对象索引：{index}。");
            filtered.Add(source[index]);
        }
        return filtered;
    }

    private static bool HasObjects(AlgorithmOutput output) =>
        output.Detections.Count > 0 || output.Segmentations.Count > 0
        || output.Events.Count > 0 || output.Classifications.Count > 0;
}

public sealed record PythonPostProcessResult(AlgorithmOutput Output, DecisionResult Decision);
