using System.ComponentModel.DataAnnotations;

namespace VisionWorkbench.Persistence;

/// <summary>检测任务（配方）持久化实体，表 Tasks（文档 §20.1）。</summary>
public sealed class TaskEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    [Required, MaxLength(64)]
    public string CameraProviderId { get; set; } = "";

    [Required, MaxLength(512)]
    public string CameraDeviceId { get; set; } = "";

    [Required, MaxLength(200)]
    public string PluginId { get; set; } = "";

    public string? PluginVersion { get; set; }
    public string? ModelId { get; set; }

    /// <summary>插件设置 JSON。</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>规则列表 JSON（InspectionRule[]）。</summary>
    public string RulesJson { get; set; } = "[]";

    /// <summary>区域定义 JSON（v1 只用单 ROI）。</summary>
    public string? RegionsJson { get; set; }

    public string? TriggerJson { get; set; }
    public string? StoragePolicyJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>批次实体，表 Batches（文档 §20.1，CNT-L-012）。</summary>
public sealed class BatchEntity
{
    [Key]
    public long Id { get; set; }

    public long TaskId { get; set; }
    public TaskEntity? Task { get; set; }

    /// <summary>人类可读批次号：yyyyMMdd-HHmmss。</summary>
    [Required, MaxLength(32)]
    public string BatchNumber { get; set; } = "";

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    /// <summary>running / completed / aborted。</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "running";

    public long InitialCounterValue { get; set; }
    public long? FinalCounterValue { get; set; }
}

/// <summary>单次检测记录，表 InspectionRecords（文档 §20.1）。</summary>
public sealed class InspectionRecordEntity
{
    [Key]
    public long Id { get; set; }

    public long TaskId { get; set; }
    public TaskEntity? Task { get; set; }

    public long? BatchId { get; set; }
    public BatchEntity? Batch { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>ok / ng / review_required / error。</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "error";

    /// <summary>NG/待确认保存原图（文档 §20.3）。</summary>
    public string? OriginalImagePath { get; set; }
    public string? AnnotatedImagePath { get; set; }

    public double AlgorithmElapsedMs { get; set; }
    public double TotalElapsedMs { get; set; }

    public string? PluginVersion { get; set; }
    public string? ModelVersion { get; set; }

    /// <summary>算法原始输出（RawResultJson）与最终判定（FinalResultJson）分开保存（文档 §20.2）。</summary>
    public string RawResultJson { get; set; } = "{}";
    public string FinalResultJson { get; set; } = "{}";

    public bool WasCorrected { get; set; }
}

/// <summary>计数事件实体，表 CountingEvents（文档 §20.1；累计数据由事件产生）。</summary>
public sealed class CountingEventEntity
{
    [Key]
    public long Id { get; set; }

    public long? RecordId { get; set; }
    public InspectionRecordEntity? Record { get; set; }

    public long? BatchId { get; set; }
    public BatchEntity? Batch { get; set; }

    [Required, MaxLength(128)]
    public string CounterId { get; set; } = "";

    public string? TrackId { get; set; }

    [Required, MaxLength(128)]
    public string EventType { get; set; } = "";

    public string? Direction { get; set; }
    public string? ClassId { get; set; }
    public long Delta { get; set; }
    public double Confidence { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public long FrameSequence { get; set; }
    public string? EvidenceImagePath { get; set; }
}

/// <summary>行为/异常事件实体，表 VisionEvents（文档 §20.1）。</summary>
public sealed class VisionEventEntity
{
    [Key]
    public long Id { get; set; }

    public long? RecordId { get; set; }
    public InspectionRecordEntity? Record { get; set; }

    public long? BatchId { get; set; }
    public BatchEntity? Batch { get; set; }

    [Required, MaxLength(128)]
    public string EventType { get; set; } = "";

    [Required, MaxLength(16)]
    public string Phase { get; set; } = "";

    [Required, MaxLength(16)]
    public string Severity { get; set; } = "info";

    public string? SubjectId { get; set; }
    public string? RegionId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public double Confidence { get; set; }
    public string? EvidenceImagePath { get; set; }
}

/// <summary>人工纠错实体，表 Corrections（文档 §20.1）。</summary>
public sealed class CorrectionEntity
{
    [Key]
    public long Id { get; set; }

    public long? RecordId { get; set; }
    public InspectionRecordEntity? Record { get; set; }

    /// <summary>修改前状态 JSON（计数/判定）。</summary>
    public string BeforeJson { get; set; } = "{}";

    /// <summary>修改后状态 JSON。</summary>
    public string AfterJson { get; set; } = "{}";

    [Required, MaxLength(1000)]
    public string Reason { get; set; } = "";

    [Required, MaxLength(64)]
    public string OperatorName { get; set; } = "";

    public DateTime CorrectedAt { get; set; } = DateTime.UtcNow;
}
