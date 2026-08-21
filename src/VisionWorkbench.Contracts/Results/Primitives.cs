namespace VisionWorkbench.Contracts.Results;

/// <summary>归一化矩形（0~1，文档 §14.3：坐标统一使用归一化坐标）。</summary>
public sealed record NormalizedRect
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public bool Contains(double nx, double ny) =>
        nx >= X && nx < X + Width && ny >= Y && ny < Y + Height;

    public bool Overlaps(NormalizedRect other) =>
        X < other.X + other.Width && X + Width > other.X &&
        Y < other.Y + other.Height && Y + Height > other.Y;
}

/// <summary>检测框结果（Detection 输出原语）。</summary>
public sealed record DetectionResult
{
    public string ClassId { get; init; } = "";
    public string ClassName { get; init; } = "";
    public double Confidence { get; init; }
    public required NormalizedRect Box { get; init; }
    public string? TrackId { get; init; }

    /// <summary>缺陷严重等级（info/warning/critical 或自定义数字字符串）。</summary>
    public string? Severity { get; init; }

    /// <summary>目标/缺陷在整幅图中的归一化面积占比。</summary>
    public double AreaRatio { get; init; }
}

/// <summary>分类结果（Classification 输出原语）。</summary>
public sealed record ClassificationResult
{
    public string ClassId { get; init; } = "";
    public string ClassName { get; init; } = "";
    public double Confidence { get; init; }
}

/// <summary>数值指标（Metrics 输出原语：count、area、speed 等）。</summary>
public sealed record MetricResult
{
    public string Name { get; init; } = "";
    public double Value { get; init; }
    public string? Unit { get; init; }
}

/// <summary>性能信息（Performance 输出原语）。</summary>
public sealed record PerformanceInfo
{
    /// <summary>纯推理耗时（毫秒）。</summary>
    public double InferenceMs { get; init; }

    /// <summary>含前后处理的总耗时（毫秒）。</summary>
    public double TotalMs { get; init; }

    /// <summary>实际执行设备：cpu / cuda / openvino 等。</summary>
    public string Device { get; init; } = "cpu";
}

/// <summary>轨迹点（归一化坐标 + 置信度）。</summary>
public sealed record Keypoint
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Confidence { get; init; }
}
