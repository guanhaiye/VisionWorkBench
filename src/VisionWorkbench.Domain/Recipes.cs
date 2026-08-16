using System.Text.Json.Serialization;

namespace VisionWorkbench.Domain;

/// <summary>规则种类（文档 §18）。v1 落地前六种，后四种占位。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleKind
{
    /// <summary>数量等于（RUL-001）。</summary>
    CountEquals,

    /// <summary>数量大于等于（RUL-002）。</summary>
    CountAtLeast,

    /// <summary>数量小于等于（RUL-003）。</summary>
    CountAtMost,

    /// <summary>某类别必须存在。</summary>
    ClassRequired,

    /// <summary>某类别禁止出现。</summary>
    ClassForbidden,

    /// <summary>置信度过低转人工确认（RUL-005）。</summary>
    LowConfidence,

    // ---- 以下为文档 §18 列出、v1 未实现判定逻辑的规则（占位） ----
    RegionMustHaveTarget,
    RegionForbiddenTarget,
    DefectSeverityThreshold,
    EventDurationThreshold,
}

/// <summary>单条判定规则（配方内唯一 RuleId，序列化进 Tasks.RulesJson）。</summary>
public sealed record InspectionRule
{
    public required string RuleId { get; init; }

    /// <summary>启用状态：禁用的规则不参与判定。</summary>
    public bool Enabled { get; init; } = true;

    public RuleKind Kind { get; init; }

    /// <summary>目标类别；null 表示全部类别。</summary>
    public string? ClassId { get; init; }

    /// <summary>数量类规则的目标值。</summary>
    public long ExpectedCount { get; init; }

    /// <summary>低置信度阈值（LowConfidence 规则）。</summary>
    public double MinConfidence { get; init; } = 0.6;

    /// <summary>失败时是否降级为人工确认而不是 NG。</summary>
    public bool DowngradeToReview { get; init; }
}

/// <summary>ROI 边界策略（文档 §16.2，CNT-S-006/007）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoiBoundaryPolicy
{
    /// <summary>中心点在 ROI 内即计入。</summary>
    CenterInside,

    /// <summary>完整框在 ROI 内才计入。</summary>
    FullInside,

    /// <summary>与 ROI 有任何重叠即计入。</summary>
    AnyOverlap,
}

/// <summary>检测配方 = 任务配置的领域模型（持久化为 JSON 列）。</summary>
public sealed record Recipe
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";

    public required string CameraProviderId { get; init; }
    public required string CameraDeviceId { get; init; }

    public required string PluginId { get; init; }
    public string? PluginVersion { get; init; }

    /// <summary>插件设置（透传 initialize.settings）。</summary>
    public string SettingsJson { get; init; } = "{}";

    /// <summary>ROI（归一化）；null = 整幅图。</summary>
    public Contracts.Results.NormalizedRect? Roi { get; init; }

    public RoiBoundaryPolicy RoiPolicy { get; init; } = RoiBoundaryPolicy.CenterInside;

    public IReadOnlyList<InspectionRule> Rules { get; init; } = [];
}
