using VisionWorkbench.Contracts.Protocol;

namespace VisionWorkbench.Contracts.Results;

/// <summary>统一算法输出（文档 §14.3）。Worker 的 result 消息负载即此结构（camelCase JSON）。</summary>
public sealed record AlgorithmOutput
{
    public required string OutputId { get; init; }
    public required string InputId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public long Sequence { get; init; }

    public IReadOnlyList<ClassificationResult> Classifications { get; init; } = [];
    public IReadOnlyList<DetectionResult> Detections { get; init; } = [];
    public IReadOnlyList<SegmentationResult> Segmentations { get; init; } = [];
    public IReadOnlyList<KeypointResult> Keypoints { get; init; } = [];
    public IReadOnlyList<TrackResult> Tracks { get; init; } = [];
    public IReadOnlyList<MetricResult> Metrics { get; init; } = [];
    public IReadOnlyList<CountingEvent> CountingEvents { get; init; } = [];
    public IReadOnlyList<VisionEvent> Events { get; init; } = [];

    public DecisionResult? Decision { get; init; }
    public PerformanceInfo? Performance { get; init; }

    /// <summary>获取 count 指标；无指标时回退为检测框数量。</summary>
    public long GetCount(string? classId = null)
    {
        var metric = Metrics.FirstOrDefault(m =>
            string.Equals(m.Name, "count", StringComparison.OrdinalIgnoreCase));
        if (metric is not null && string.IsNullOrEmpty(classId))
        {
            return (long)metric.Value;
        }
        return Detections.Count(d => string.IsNullOrEmpty(classId) || d.ClassId == classId);
    }
}

/// <summary>算法输入（文档 §14.1 FrameInput 的首版文件传图形态，文档 §11.3）。</summary>
public sealed record AlgorithmInput
{
    public required string InputId { get; init; }

    /// <summary>输入图像文件路径（临时文件，由宿主写入）。</summary>
    public required string ImagePath { get; init; }

    public long FrameSequence { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>ROI（归一化）；null 表示整幅图。由宿主裁剪或交由 Worker 处理。</summary>
    public NormalizedRect? Roi { get; init; }
}

/// <summary>算法会话宿主侧异常（携带标准错误码，文档 §22.2）。</summary>
public sealed class AlgorithmFaultException : Exception
{
    public string ErrorCode { get; }

    public AlgorithmFaultException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
