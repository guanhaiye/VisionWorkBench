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
                _ => (true, false, $"规则类型 {rule.Kind} 暂未实现判定逻辑，已视为通过"),
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
}
