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

    private static double SeverityScore(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" or "严重" or "3" => 3,
        "warning" or "warn" or "警告" or "2" => 2,
        "info" or "normal" or "提示" or "1" => 1,
        _ when double.TryParse(severity, out var value) => value,
        _ => 0,
    };
}
