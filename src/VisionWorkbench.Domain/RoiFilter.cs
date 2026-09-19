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

    /// <summary>
    /// 在完整图像推理后应用 ROI，保持检测框坐标仍对应原图。
    /// ROI 不应直接裁剪模型输入，否则小区域可能丢失模型所需的上下文。
    /// </summary>
    public static AlgorithmOutput ApplyToOutput(
        AlgorithmOutput output,
        NormalizedRect? roi,
        RoiBoundaryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (roi is null)
        {
            return output;
        }

        var detections = Apply(output.Detections, roi, policy);
        var countMetricFound = false;
        var metrics = output.Metrics.Select(metric =>
        {
            if (!string.Equals(metric.Name, "count", StringComparison.OrdinalIgnoreCase))
            {
                return metric;
            }

            countMetricFound = true;
            return metric with { Value = detections.Count };
        }).ToList();
        if (!countMetricFound)
        {
            metrics.Add(new MetricResult { Name = "count", Value = detections.Count });
        }

        return output with
        {
            Detections = detections,
            Metrics = metrics,
        };
    }
}
