using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VisionWorkbench.Persistence;

/// <summary>项目实体，承载一个或多个独立工位。</summary>
public sealed class ProjectEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64)]
    public string ProjectCode { get; set; } = "";

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>工位实体。StationCode 是项目内稳定的业务编号，不能用名称或自增 ID 替代。</summary>
public sealed class StationEntity
{
    [Key]
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public ProjectEntity? Project { get; set; }

    [Required, MaxLength(64)]
    public string StationCode { get; set; } = "";

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public bool IsArchived { get; set; }
    public long? TaskId { get; set; }

    [MaxLength(64)]
    public string? CameraProviderId { get; set; }

    [MaxLength(512)]
    public string? CameraDeviceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>检测任务（配方）持久化实体，表 Tasks（文档 §20.1）。</summary>
public sealed class TaskEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64)]
    public string StationCode { get; set; } = "";

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

    /// <summary>可选实时工作流 JSON；为空表示普通任务，不影响旧任务兼容。</summary>
    public string? WorkflowJson { get; set; }

    public string? TriggerJson { get; set; }
    public string? StoragePolicyJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>SOP 产品周期聚合，表 SopRuns。</summary>
public sealed class SopRunEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(128)] public string SopDefinitionId { get; set; } = "";
    public int SopVersion { get; set; } = 1;
    [Required] public string DefinitionHash { get; set; } = "";
    [Required] public string DefinitionSnapshotJson { get; set; } = "{}";
    [Required, MaxLength(64)] public string ProjectId { get; set; } = "default";
    [Required, MaxLength(64)] public string StationCode { get; set; } = "";
    public long TaskId { get; set; }
    public long? BatchId { get; set; }
    [Required, MaxLength(128)] public string CycleId { get; set; } = "";
    public string? ProductId { get; set; }
    [Required, MaxLength(32)] public string Status { get; set; } = "Idle";
    public int CurrentStepOrder { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>产品级最终结论；无算法帧超时时也通过此字段追溯。</summary>
    [MaxLength(24)] public string? FinalStatus { get; set; }
    public string? FinalDecisionJson { get; set; }
    public string? FinalResultJson { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public DateTime? FinalPublishedAtUtc { get; set; }
    [MaxLength(96)] public string? FinalPublishClaimId { get; set; }
    public DateTime? FinalPublishClaimedAtUtc { get; set; }
}

/// <summary>SOP 步骤结果，表 SopStepResults。</summary>
public sealed class SopStepResultEntity
{
    [Key] public long Id { get; set; }
    public long SopRunId { get; set; }
    public SopRunEntity? SopRun { get; set; }
    [Required, MaxLength(128)] public string StepId { get; set; } = "";
    public int Attempt { get; set; } = 1;
    [Required, MaxLength(32)] public string Status { get; set; } = "Pending";
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public double? Confidence { get; set; }
    public string? ConditionResultJson { get; set; }
    public string? FailureReason { get; set; }
    public long? InspectionRecordId { get; set; }
}

/// <summary>批次实体，表 Batches（文档 §20.1，CNT-L-012）。</summary>
public sealed class BatchEntity
{
    [Key]
    public long Id { get; set; }

    public long TaskId { get; set; }
    public TaskEntity? Task { get; set; }

    [Required, MaxLength(64)]
    public string ProjectId { get; set; } = "default";

    [Required, MaxLength(64)]
    public string StationCode { get; set; } = "";

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

    /// <summary>检测发生时的工位编号快照，工位配置后续变化不影响历史结果归属。</summary>
    [Required, MaxLength(64)]
    public string StationCode { get; set; } = "";

    /// <summary>检测发生时的项目业务编号快照。</summary>
    [Required, MaxLength(64)]
    public string ProjectId { get; set; } = "default";

    public long TaskId { get; set; }
    public TaskEntity? Task { get; set; }

    public long? BatchId { get; set; }
    public BatchEntity? Batch { get; set; }

    public long? SopRunId { get; set; }
    public SopRunEntity? SopRun { get; set; }

    /// <summary>重新推理来源记录；重新运行永远新增记录，不覆盖旧结果。</summary>
    public long? SourceRecordId { get; set; }

    [Required, MaxLength(24)]
    public string RunType { get; set; } = "production";

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

    /// <summary>可选 SOP 产品周期/步骤快照；普通检测为空。</summary>
    public string? WorkflowResultJson { get; set; }

    public bool WasCorrected { get; set; }

    [NotMapped]
    public bool HasEvidenceImage => !string.IsNullOrWhiteSpace(AnnotatedImagePath);
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

    public long? SopRunId { get; set; }
    public SopRunEntity? SopRun { get; set; }

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
    public long? SourceTaskId { get; set; }
    public string? SourceStationCode { get; set; }
    public long? FrameSequence { get; set; }
    public string? BoxJson { get; set; }
    public string? TextValue { get; set; }
    public string? CodeValue { get; set; }
    public long? Count { get; set; }
    public string? AttributesJson { get; set; }
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

/// <summary>商用化数据库中的用户；账号数据不再依赖可直接编辑的 settings.json。</summary>
public sealed class UserEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(64)] public string UserName { get; set; } = "";
    [Required, MaxLength(512)] public string PasswordHash { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
}

public sealed class RoleEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(64)] public string Code { get; set; } = "";
    [Required, MaxLength(128)] public string Name { get; set; } = "";
}

public sealed class PermissionEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(128)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
}

public sealed class UserRoleEntity
{
    public long UserId { get; set; }
    public long RoleId { get; set; }
}

public sealed class RolePermissionEntity
{
    public long RoleId { get; set; }
    public long PermissionId { get; set; }
}

public sealed class LoginEventEntity
{
    [Key] public long Id { get; set; }
    public long? UserId { get; set; }
    [Required, MaxLength(64)] public string UserName { get; set; } = "";
    public bool Succeeded { get; set; }
    [Required, MaxLength(256)] public string Reason { get; set; } = "";
    [MaxLength(128)] public string? ClientAddress { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>追加式审计事件。Hash/PreviousHash 用于发现普通文件或数据库层面的篡改。</summary>
public sealed class AuditEventEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(40)] public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public long? ActorUserId { get; set; }
    [Required, MaxLength(64)] public string ActorName { get; set; } = "system";
    [Required, MaxLength(128)] public string Action { get; set; } = "";
    [Required, MaxLength(64)] public string ObjectType { get; set; } = "";
    [MaxLength(256)] public string? ObjectId { get; set; }
    [Required, MaxLength(32)] public string Result { get; set; } = "success";
    public string DetailsJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(64)] public string? PreviousHash { get; set; }
    [Required, MaxLength(64)] public string Hash { get; set; } = "";
}

public sealed class BackupRecordEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(1024)] public string Path { get; set; } = "";
    [Required, MaxLength(32)] public string Kind { get; set; } = "manual";
    [Required, MaxLength(32)] public string Status { get; set; } = "success";
    [MaxLength(64)] public string? Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(1000)] public string? Error { get; set; }
}

public sealed class HealthSnapshotEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(32)] public string State { get; set; } = "Healthy";
    public double CpuUsage { get; set; }
    public ulong MemoryUsedBytes { get; set; }
    public ulong MemoryTotalBytes { get; set; }
    public ulong GpuUsedBytes { get; set; }
    public ulong GpuTotalBytes { get; set; }
    public ulong FreeDiskBytes { get; set; }
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public string DetailsJson { get; set; } = "{}";
}

public sealed class AlertEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(64)] public string Code { get; set; } = "";
    [Required, MaxLength(16)] public string Severity { get; set; } = "warning";
    [Required, MaxLength(256)] public string Title { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    [Required, MaxLength(16)] public string Status { get; set; } = "active";
    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime? RecoveredAtUtc { get; set; }
    [MaxLength(64)] public string? AcknowledgedBy { get; set; }
}

/// <summary>通信幂等记录，重启后仍能识别已完成 requestId。</summary>
public sealed class CommunicationRequestEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(64)] public string ProjectCode { get; set; } = "default";
    [Required, MaxLength(128)] public string ClientId { get; set; } = "";
    [Required, MaxLength(128)] public string RequestId { get; set; } = "";
    [Required, MaxLength(64)] public string Command { get; set; } = "";
    [Required, MaxLength(64)] public string RequestHash { get; set; } = "";
    [Required, MaxLength(32)] public string Status { get; set; } = "processing";
    public string? ResponseJson { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
}

public sealed class RecipeVersionEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(128)] public string RecipeCode { get; set; } = "";
    public int Version { get; set; }
    [Required, MaxLength(16)] public string State { get; set; } = "draft";
    [Required] public string DefinitionJson { get; set; } = "{}";
    [Required, MaxLength(64)] public string ContentSha256 { get; set; } = "";
    [MaxLength(64)] public string? PublishedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
}

public sealed class ModelArtifactEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(128)] public string ModelCode { get; set; } = "";
    public int Version { get; set; }
    [Required, MaxLength(260)] public string FilePath { get; set; } = "";
    [Required, MaxLength(64)] public string Sha256 { get; set; } = "";
    [MaxLength(64)] public string? Algorithm { get; set; }
    [Required, MaxLength(16)] public string State { get; set; } = "draft";
    [MaxLength(64)] public string? PublishedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
}

public sealed class DeploymentBindingEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(128)] public string TargetCode { get; set; } = "";
    [Required, MaxLength(128)] public string RecipeCode { get; set; } = "";
    public int RecipeVersion { get; set; }
    [Required, MaxLength(128)] public string ModelCode { get; set; } = "";
    public int ModelVersion { get; set; }
    [Required, MaxLength(16)] public string State { get; set; } = "active";
    [MaxLength(64)] public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LicenseEventEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(64)] public string LicenseId { get; set; } = "";
    [Required, MaxLength(32)] public string EventType { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DatasetVersionEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(128)] public string DatasetCode { get; set; } = "";
    public int Version { get; set; }
    [Required, MaxLength(64)] public string Sha256 { get; set; } = "";
    public int TrainingCount { get; set; }
    public int ValidationCount { get; set; }
    [MaxLength(64)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ReportJobEntity
{
    [Key] public long Id { get; set; }
    [Required, MaxLength(32)] public string Status { get; set; } = "queued";
    [Required, MaxLength(64)] public string Format { get; set; } = "csv";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    [MaxLength(260)] public string? OutputPath { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
