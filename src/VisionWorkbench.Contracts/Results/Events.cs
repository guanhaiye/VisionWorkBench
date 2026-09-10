namespace VisionWorkbench.Contracts.Results;

/// <summary>计数事件类型（文档 §16.8）。</summary>
public enum CountingEventType
{
    Appeared,
    EnteredZone,
    ExitedZone,
    CrossedLine,
    Accepted,
    Rejected,
    Uncertain,
    Corrected,
    CounterReset,
}

/// <summary>计数事件（文档 §16.8）。累计数据由事件产生，不允许只存总数。</summary>
public sealed record CountingEvent
{
    public required string EventId { get; init; }
    public required string CounterId { get; init; }
    public string? TrackId { get; init; }
    public string? ClassId { get; init; }
    public required CountingEventType Type { get; init; }
    /// <summary>forward / reverse / null。</summary>
    public string? Direction { get; init; }
    public long Delta { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public long FrameSequence { get; init; }
    public string? EvidenceImagePath { get; init; }
}

/// <summary>行为/异常事件阶段（文档 §17.2）。</summary>
public enum VisionEventPhase
{
    Started,
    Updated,
    Completed,
    Cancelled,
}

public enum VisionEventSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>行为或异常事件（VisionEvent 输出原语）。</summary>
public sealed record VisionEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public VisionEventPhase Phase { get; init; } = VisionEventPhase.Started;
    public VisionEventSeverity Severity { get; init; } = VisionEventSeverity.Warning;
    public string? SubjectId { get; init; }
    public string? RegionId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public double Confidence { get; init; }
    public string? Message { get; init; }
    public string? EvidenceImagePath { get; init; }

    // 可选来源字段：旧 Worker 不提供时保持 null，兼容现有插件协议。
    public long? SourceTaskId { get; init; }
    public string? SourceStationCode { get; init; }
    public long? FrameSequence { get; init; }
    public NormalizedRect? Box { get; init; }
    public string? TextValue { get; init; }
    public string? CodeValue { get; init; }
    public long? Count { get; init; }
    public string? AttributesJson { get; init; }
}
