using System.Text.Json.Serialization;

namespace VisionWorkbench.Domain;

/// <summary>规则种类（文档 §18）。</summary>
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

    // ---- 区域、缺陷和行为规则 ----
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

    /// <summary>严重度或面积阈值。严重度使用 1/2/3 对应 info/warning/critical。</summary>
    public double Threshold { get; init; }

    /// <summary>事件持续时间阈值（毫秒）。</summary>
    public double DurationMs { get; init; }

    /// <summary>可选区域/事件类型标识；兼容旧配置时为空表示全部。</summary>
    public string? RegionId { get; init; }
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

/// <summary>计数模式（文档 §16.2 快照 / §16.3 动态去重 / §16.4 跨线）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CountingMode
{
    /// <summary>快照计数：逐帧独立检测，无跟踪状态。</summary>
    Snapshot,

    /// <summary>动态去重：目标跟踪确认后累计一次（UniqueTracking）。</summary>
    UniqueTracking,

    /// <summary>跨线计数：穿过检测线累计，含正反向（LineCrossing）。</summary>
    LineCrossing,
}

/// <summary>检测线配置（LineCrossing 模式，归一化坐标）。</summary>
public sealed record CountingLineConfig
{
    public required Contracts.Results.NormalizedPoint A { get; init; }
    public required Contracts.Results.NormalizedPoint B { get; init; }

    /// <summary>滞回带半宽（归一化，带内抖动不计，CNT-L-003）。</summary>
    public double Hysteresis { get; init; } = 0.02;
}

/// <summary>检测配方 = 任务配置的领域模型（持久化为 JSON 列）。</summary>
public sealed record Recipe
{
    /// <summary>工位编号。项目内唯一，并随检测结果一同输出给外部设备。</summary>
    public string StationCode { get; init; } = "";

    public required string Name { get; init; }
    public string Description { get; init; } = "";

    public required string CameraProviderId { get; init; }
    public required string CameraDeviceId { get; init; }

    public required string PluginId { get; init; }
    public string? PluginVersion { get; init; }

    /// <summary>推理设备：cpu 或 cuda。</summary>
    public string ExecutionProvider { get; init; } = "cpu";

    /// <summary>插件设置（透传 initialize.settings）。</summary>
    public string SettingsJson { get; init; } = "{}";

    /// <summary>ROI（归一化）；null = 整幅图。</summary>
    public Contracts.Results.NormalizedRect? Roi { get; init; }

    public RoiBoundaryPolicy RoiPolicy { get; init; } = RoiBoundaryPolicy.CenterInside;

    /// <summary>计数模式：流水模式（Unique/Line）无 OK/NG 判定，允许零规则。</summary>
    public CountingMode CountingMode { get; init; } = CountingMode.Snapshot;

    /// <summary>检测线（LineCrossing 模式）；null = 用插件默认竖直中线。</summary>
    public CountingLineConfig? CountingLine { get; init; }

    public IReadOnlyList<InspectionRule> Rules { get; init; } = [];
}
