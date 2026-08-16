namespace VisionWorkbench.Contracts.Results;

/// <summary>跟踪结果（Tracking 输出原语：Track ID 与轨迹）。</summary>
public sealed record TrackResult
{
    public string TrackId { get; init; } = "";
    public string ClassId { get; init; } = "";
    public required NormalizedRect Box { get; init; }

    /// <summary>归一化轨迹（最近 N 帧）。</summary>
    public IReadOnlyList<NormalizedPoint> Trail { get; init; } = [];

    /// <summary>该轨迹已连续存在的帧数。</summary>
    public int Age { get; init; }

    /// <summary>距上次成功匹配的帧数。</summary>
    public int TimeSinceUpdate { get; init; }
}

public sealed record NormalizedPoint
{
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>分割结果（Segmentation 输出原语）。首版以轮廓点集表达，掩膜文件路径可选。</summary>
public sealed record SegmentationResult
{
    public string ClassId { get; init; } = "";
    public double Confidence { get; init; }
    public IReadOnlyList<IReadOnlyList<NormalizedPoint>> Contours { get; init; } = [];
    public double AreaRatio { get; init; }
    public string? MaskImagePath { get; init; }
}

/// <summary>关键点结果（Keypoints 输出原语）。</summary>
public sealed record KeypointResult
{
    public string? TrackId { get; init; }
    public string ClassId { get; init; } = "";
    public IReadOnlyList<Keypoint> Points { get; init; } = [];
}
