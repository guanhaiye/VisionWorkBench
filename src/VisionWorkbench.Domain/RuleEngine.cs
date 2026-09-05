using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Domain;

/// <summary>规则引擎：算法输出事实 → 业务判定（文档 §18）。</summary>
public static class RuleEngine
{
    /// <summary>
    /// 依次评估全部启用规则；任何 NG 失败 → Ng，否则任何降级失败 → ReviewRequired，否则 Ok。
    /// ROI 先过滤，数量与类别规则只统计 ROI 内的检测（文档 §16.2）。
    /// </summary>
    public static DecisionResult Evaluate(
        AlgorithmOutput output,
        IReadOnlyList<InspectionRule> rules,
        NormalizedRect? roi = null,
        RoiBoundaryPolicy roiPolicy = RoiBoundaryPolicy.CenterInside)
    {
        ArgumentNullException.ThrowIfNull(output);

        var inRoi = RoiFilter.Apply(output.Detections, roi, roiPolicy);
        var outcomes = new List<RuleOutcome>();
        var anyNg = false;
        var anyReview = false;

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            var (passed, review, message) = rule.Kind switch
            {
                RuleKind.CountEquals => CountRule(inRoi, rule, v => v == rule.ExpectedCount),
                RuleKind.CountAtLeast => CountRule(inRoi, rule, v => v >= rule.ExpectedCount),
                RuleKind.CountAtMost => CountRule(inRoi, rule, v => v <= rule.ExpectedCount),
                RuleKind.ClassRequired => ClassRule(inRoi, rule, required: true),
                RuleKind.ClassForbidden => ClassRule(inRoi, rule, required: false),
                RuleKind.LowConfidence => ConfidenceRule(inRoi, rule),
                RuleKind.AreaRange => RangeRule(BuildMeasurements(output, roi, roiPolicy), rule, MeasurementKind.Area),
                RuleKind.DiameterRange => RangeRule(BuildMeasurements(output, roi, roiPolicy), rule, MeasurementKind.Diameter),
                RuleKind.CountRange => CountRangeRule(BuildMeasurements(output, roi, roiPolicy), rule),
                RuleKind.RegionMustHaveTarget => ClassRule(inRoi, rule, required: true),
                RuleKind.RegionForbiddenTarget => ClassRule(inRoi, rule, required: false),
                RuleKind.DefectSeverityThreshold => DefectThresholdRule(inRoi, rule),
                RuleKind.EventDurationThreshold => EventDurationRule(output, rule),
                _ => (false, false, $"未知规则类型 {rule.Kind}"),
            };
            if (!passed)
            {
                if (review || rule.DowngradeToReview)
                {
                    anyReview = true;
                }
                else
                {
                    anyNg = true;
                }
            }
            outcomes.Add(new RuleOutcome
            {
                RuleId = rule.RuleId,
                RuleKind = rule.Kind.ToString(),
                Passed = passed,
                Message = message,
            });
        }

        var status = anyNg ? DecisionStatus.Ng
            : anyReview ? DecisionStatus.ReviewRequired
            : DecisionStatus.Ok;
        return new DecisionResult { Status = status, Outcomes = outcomes };
    }

    private static (bool passed, bool review, string message) CountRule(
        IReadOnlyList<DetectionResult> detections, InspectionRule rule, Func<long, bool> predicate)
    {
        var actual = detections.Count(d =>
            string.IsNullOrEmpty(rule.ClassId) || d.ClassId == rule.ClassId);
        var passed = predicate(actual);
        return (passed, false,
            passed ? $"{rule.RuleId}: 数量 {actual} 满足要求"
                   : $"{rule.RuleId}: 数量 {actual} 不满足要求（期望 {rule.ExpectedCount}，类别 {rule.ClassId ?? "全部"}）");
    }

    private static (bool passed, bool review, string message) ClassRule(
        IReadOnlyList<DetectionResult> detections, InspectionRule rule, bool required)
    {
        var present = detections.Any(d => d.ClassId == rule.ClassId);
        var passed = required ? present : !present;
        return (passed, false,
            passed ? $"{rule.RuleId}: 类别 {rule.ClassId} 条件满足"
                   : $"{rule.RuleId}: 类别 {rule.ClassId} " + (required ? "缺失" : "禁止出现但存在"));
    }

    private static (bool passed, bool review, string message) ConfidenceRule(
        IReadOnlyList<DetectionResult> detections, InspectionRule rule)
    {
        var weakest = detections
            .Where(d => string.IsNullOrEmpty(rule.ClassId) || d.ClassId == rule.ClassId)
            .Select(d => d.Confidence)
            .DefaultIfEmpty(1.0)
            .Min();
        var passed = weakest >= rule.MinConfidence;
        return (passed, !passed,
            passed ? $"{rule.RuleId}: 最低置信度 {weakest:0.00} ≥ {rule.MinConfidence:0.00}"
                   : $"{rule.RuleId}: 存在低置信度检测（最低 {weakest:0.00} < {rule.MinConfidence:0.00}），转人工确认");
    }

    private static (bool passed, bool review, string message) DefectThresholdRule(
        IReadOnlyList<DetectionResult> detections, InspectionRule rule)
    {
        var threshold = rule.Threshold > 0 ? rule.Threshold : rule.MinConfidence;
        var matches = detections.Where(d => string.IsNullOrEmpty(rule.ClassId) || d.ClassId == rule.ClassId).ToArray();
        var exceeded = matches.FirstOrDefault(d =>
            d.AreaRatio >= threshold || SeverityScore(d.Severity) >= threshold);
        var passed = exceeded is null;
        return (passed, false,
            passed ? $"{rule.RuleId}: 缺陷严重度/面积未超过 {threshold:0.###}"
                   : $"{rule.RuleId}: 类别 {exceeded!.ClassId} 的缺陷严重度或面积超过 {threshold:0.###}");
    }

    private static (bool passed, bool review, string message) EventDurationRule(
        AlgorithmOutput output, InspectionRule rule)
    {
        var threshold = rule.DurationMs > 0 ? rule.DurationMs : rule.ExpectedCount;
        var events = output.Events.Where(e => string.IsNullOrEmpty(rule.ClassId)
            || string.Equals(e.EventType, rule.ClassId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exceeded = events.FirstOrDefault(e => e.StartedAt is { } start && e.EndedAt is { } end
            && (end - start).TotalMilliseconds > threshold);
        var passed = exceeded is null;
        return (passed, true,
            passed ? $"{rule.RuleId}: 事件持续时间未超过 {threshold:0.#}ms"
                   : $"{rule.RuleId}: 事件 {exceeded!.EventType} 持续时间超过 {threshold:0.#}ms");
    }

    private enum MeasurementKind
    {
        Area,
        Diameter,
    }

    private readonly record struct ObjectMeasurement(string ClassId, double Area, double Diameter);

    private static (bool passed, bool review, string message) CountRangeRule(
        IReadOnlyList<ObjectMeasurement> measurements, InspectionRule rule)
    {
        var actual = measurements.Count(m => string.IsNullOrEmpty(rule.ClassId) || m.ClassId == rule.ClassId);
        var passed = actual > 0
            && (rule.Minimum <= 0 || actual >= rule.Minimum)
            && (rule.Maximum <= 0 || actual <= rule.Maximum);
        return (passed, false,
            passed
                ? $"{rule.RuleId}: 筛选后保留 {actual} 个对象"
                : $"{rule.RuleId}: 筛选后没有对象（数量范围 [{FormatBound(rule.Minimum)}, {FormatBound(rule.Maximum)}]）");
    }

    private static (bool passed, bool review, string message) RangeRule(
        IReadOnlyList<ObjectMeasurement> measurements, InspectionRule rule, MeasurementKind kind)
    {
        var matches = measurements
            .Where(m => string.IsNullOrEmpty(rule.ClassId) || m.ClassId == rule.ClassId)
            .Select(m => kind == MeasurementKind.Area ? m.Area : m.Diameter)
            .ToArray();
        var passed = matches.Any(value =>
            (rule.Minimum <= 0 || value >= rule.Minimum)
            && (rule.Maximum <= 0 || value <= rule.Maximum));
        var label = kind == MeasurementKind.Area ? "面积占比" : "直径";
        return (passed, false,
            passed
                ? $"{rule.RuleId}: 按{label}筛选后仍有对象"
                : $"{rule.RuleId}: 按{label}筛选后没有对象（范围 [{FormatBound(rule.Minimum)}, {FormatBound(rule.Maximum)}]）");
    }

    private static IReadOnlyList<ObjectMeasurement> BuildMeasurements(
        AlgorithmOutput output, NormalizedRect? roi, RoiBoundaryPolicy roiPolicy)
    {
        if (output.Segmentations.Count > 0)
        {
            return output.Segmentations
                .Where(s => roi is null || IsContourInRoi(s.Contours, roi, roiPolicy))
                .Select(s => new ObjectMeasurement(
                    s.ClassId,
                    PixelArea(s.AreaRatio, output),
                    ContourDiameter(s.Contours, output.ImageWidth, output.ImageHeight)))
                .ToArray();
        }

        return RoiFilter.Apply(output.Detections, roi, roiPolicy)
            .Select(d => new ObjectMeasurement(
                d.ClassId,
                PixelArea(d.AreaRatio > 0 ? d.AreaRatio : Math.Max(0, d.Box.Width * d.Box.Height), output),
                PixelDiameter(d.Box.Width, d.Box.Height, output)))
            .ToArray();
    }

    private static bool IsContourInRoi(
        IReadOnlyList<IReadOnlyList<NormalizedPoint>> contours,
        NormalizedRect roi,
        RoiBoundaryPolicy policy)
    {
        var points = contours.SelectMany(c => c).ToArray();
        if (points.Length == 0)
        {
            return true;
        }
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        var box = new NormalizedRect
        {
            X = minX,
            Y = minY,
            Width = Math.Max(0, maxX - minX),
            Height = Math.Max(0, maxY - minY),
        };
        return policy switch
        {
            RoiBoundaryPolicy.CenterInside => roi.Contains(box.CenterX, box.CenterY),
            RoiBoundaryPolicy.FullInside => roi.Contains(box.X, box.Y)
                && roi.Contains(box.X + box.Width, box.Y + box.Height),
            RoiBoundaryPolicy.AnyOverlap => roi.Overlaps(box),
            _ => true,
        };
    }

    private static double ContourDiameter(
        IReadOnlyList<IReadOnlyList<NormalizedPoint>> contours, int imageWidth, int imageHeight)
    {
        var points = contours.SelectMany(c => c).ToArray();
        if (points.Length == 0)
        {
            return 0;
        }
        return PixelDiameter(points.Max(p => p.X) - points.Min(p => p.X),
            points.Max(p => p.Y) - points.Min(p => p.Y), imageWidth, imageHeight);
    }

    private static double PixelArea(double normalizedArea, AlgorithmOutput output) =>
        normalizedArea * Math.Max(0, output.ImageWidth) * Math.Max(0, output.ImageHeight);

    private static double PixelDiameter(double normalizedWidth, double normalizedHeight, AlgorithmOutput output) =>
        PixelDiameter(normalizedWidth, normalizedHeight, output.ImageWidth, output.ImageHeight);

    private static double PixelDiameter(
        double normalizedWidth, double normalizedHeight, int imageWidth, int imageHeight) =>
        Math.Max(normalizedWidth * Math.Max(0, imageWidth), normalizedHeight * Math.Max(0, imageHeight));

    private static string FormatBound(double value) => value <= 0 ? "不限" : value.ToString("0.###");

    private static double SeverityScore(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" or "严重" or "3" => 3,
        "warning" or "warn" or "警告" or "2" => 2,
        "info" or "normal" or "提示" or "1" => 1,
        _ when double.TryParse(severity, out var value) => value,
        _ => 0,
    };
}
