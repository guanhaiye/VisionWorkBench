namespace VisionWorkbench.Contracts.Results;

/// <summary>统一判定状态（文档 §18）。</summary>
public enum DecisionStatus
{
    Ok,
    Ng,
    ReviewRequired,
    Unknown,
    Error,
}

/// <summary>单条规则的判定明细。</summary>
public sealed record RuleOutcome
{
    /// <summary>规则标识（配方内唯一）。</summary>
    public required string RuleId { get; init; }
    public required string RuleKind { get; init; }
    public bool Passed { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>规则引擎判定结果：算法输出事实，规则引擎完成业务判定。</summary>
public sealed record DecisionResult
{
    public DecisionStatus Status { get; init; } = DecisionStatus.Unknown;

    /// <summary>全部规则的判定明细（含通过与否，便于追溯）。</summary>
    public IReadOnlyList<RuleOutcome> Outcomes { get; init; } = [];

    /// <summary>未通过的规则（NG 或待确认原因）。</summary>
    public IReadOnlyList<RuleOutcome> Failed => Outcomes.Where(o => !o.Passed).ToArray();

    public string Summary
    {
        get
        {
            var failed = Failed;
            return failed.Count == 0 ? "全部规则通过" : string.Join("；", failed.Select(f => f.Message));
        }
    }
}
