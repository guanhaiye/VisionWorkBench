using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Domain;

/// <summary>ROI 过滤（文档 §16.2 边界规则，CNT-S-006/007）。</summary>
public static class RoiFilter
{
    /// <summary>按策略过滤检测框；roi 为 null 时原样返回。</summary>
    public static IReadOnlyList<DetectionResult> Apply(
        IReadOnlyList<DetectionResult> detections,
        NormalizedRect? roi,
        RoiBoundaryPolicy policy)
    {
        if (roi is null)
        {
            return detections;
        }
        return detections.Where(d => policy switch
        {
            RoiBoundaryPolicy.CenterInside =>
                roi.Contains(d.Box.CenterX, d.Box.CenterY),
            RoiBoundaryPolicy.FullInside =>
                roi.Contains(d.Box.X, d.Box.Y) &&
                roi.Contains(d.Box.X + d.Box.Width, d.Box.Y + d.Box.Height),
            RoiBoundaryPolicy.AnyOverlap =>
                roi.Overlaps(d.Box),
            _ => true,
        }).ToArray();
    }
}
