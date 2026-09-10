using System.Text.Json.Serialization;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Domain;

/// <summary>实时任务可选的 SOP 运行模式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SopRunMode
{
    StrictOrder,
    AllowSkip,
    ManualReview,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SopDefinitionStatus
{
    Draft,
    Published,
    Retired,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SopConditionKind
{
    ObjectPresent,
    ObjectAbsent,
    ObjectCount,
    ObjectInRegion,
    ObjectStable,
    TextEquals,
    CodeEquals,
    BehaviorStarted,
    BehaviorCompleted,
    RegionChanged,
    CustomEvent,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SopRunStatus
{
    Idle,
    WaitingForStep,
    Stabilizing,
    StepCompleted,
    CompletedOk,
    NgTimeout,
    NgConditionFailed,
    NgWrongOrder,
    ReviewRequired,
    Interrupted,
    Aborted,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SopStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
    ReviewRequired,
}

/// <summary>任务配方中的可选 SOP 绑定。Definition 是第一版的内嵌快照，后续可替换为目录引用。</summary>
public sealed record SopBinding
{
    public required string DefinitionId { get; init; }
    public int Version { get; init; } = 1;
    public SopRunMode RunMode { get; init; } = SopRunMode.StrictOrder;
    public SopDefinition? Definition { get; init; }
}

/// <summary>
/// SOP 自己维护的运行配方。它不属于普通检测任务，保存 SOP 时由系统同步为独立的运行入口。
/// </summary>
public sealed record SopExecutionProfile
{
    public string StationCode { get; init; } = "";
    public required string CameraProviderId { get; init; }
    public required string CameraDeviceId { get; init; }
    /// <summary>
    /// 第一版单模型配置的兼容字段。新 SOP 不再在顶层选择模型，实际模型配置位于每个 SopStep.Execution。
    /// 保存时该字段由第一步模型自动回填，便于复用现有 Recipe/任务运行入口。
    /// </summary>
    public string PluginId { get; init; } = "";
    public string ExecutionProvider { get; init; } = "cpu";
    public InspectionTaskType TaskType { get; init; } = InspectionTaskType.Detection;
    public string SettingsJson { get; init; } = "{}";
    public Contracts.Results.NormalizedRect? Roi { get; init; }
    public RoiBoundaryPolicy RoiPolicy { get; init; } = RoiBoundaryPolicy.CenterInside;
    public CountingMode CountingMode { get; init; } = CountingMode.Snapshot;
    public CountingLineConfig? CountingLine { get; init; }
    public BehaviorRecognitionConfig Behavior { get; init; } = new();
    public PostProcessConfig PostProcess { get; init; } = new();
    public IReadOnlyList<InspectionRule> Rules { get; init; } = [];
    public SopRunMode RunMode { get; init; } = SopRunMode.StrictOrder;
}

/// <summary>
/// SOP 工序的独立模型执行配置。一个 SOP 可以由多个工序组成，每个工序使用不同模型、插件和参数。
/// </summary>
public sealed record SopStepExecution
{
    public string ModelId { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string PluginId { get; init; } = "";
    public InspectionTaskType TaskType { get; init; } = InspectionTaskType.Detection;
    public string ExecutionProvider { get; init; } = "cpu";
    public string SettingsJson { get; init; } = "{}";
    public NormalizedRect? Roi { get; init; }
    public RoiBoundaryPolicy RoiPolicy { get; init; } = RoiBoundaryPolicy.CenterInside;
    public IReadOnlyList<InspectionRule> Rules { get; init; } = [];
}

/// <summary>SOP 产品周期聚合根；一个周期可以跨越多个检测帧。</summary>
public sealed record SopRun
{
    public long Id { get; init; }
    public required string SopDefinitionId { get; init; }
    public int SopVersion { get; init; }
    public required string DefinitionHash { get; init; }
    public required string DefinitionSnapshotJson { get; init; }
    public required string ProjectId { get; init; }
    public required string StationCode { get; init; }
    public long TaskId { get; init; }
    public long? BatchId { get; init; }
    public required string CycleId { get; init; }
    public string? ProductId { get; init; }
    public SopRunStatus Status { get; init; }
    public int CurrentStepOrder { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record SopStepResult
{
    public long Id { get; init; }
    public long SopRunId { get; init; }
    public required string StepId { get; init; }
    public int Attempt { get; init; } = 1;
    public SopStepStatus Status { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public double? Confidence { get; init; }
    public string? ConditionResultJson { get; init; }
    public string? FailureReason { get; init; }
    public long? InspectionRecordId { get; init; }
}

public sealed record SopDefinition
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string ProductCode { get; init; } = "";
    public int Version { get; init; } = 1;
    public SopDefinitionStatus Status { get; init; } = SopDefinitionStatus.Published;
    public SopExecutionProfile? Execution { get; init; }
    public IReadOnlyList<SopStep> Steps { get; init; } = [];
}

public sealed record SopStep
{
    public required string Id { get; init; }
    public int Order { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool Required { get; init; } = true;
    public bool EnforceOrder { get; init; } = true;
    public double TimeoutSeconds { get; init; } = 30;
    public int MinimumStableFrames { get; init; } = 3;
    /// <summary>本工序使用的模型。为空时回退到旧版 SOP 顶层 Execution 配置。</summary>
    public SopStepExecution? Execution { get; init; }
    public IReadOnlyList<SopCondition> Conditions { get; init; } = [];
}

public sealed record SopCondition
{
    public required string Id { get; init; }
    public SopConditionKind Kind { get; init; } = SopConditionKind.CustomEvent;
    public string? EventType { get; init; }
    public string? ClassId { get; init; }
    public string? TextValue { get; init; }
    public string? CodeValue { get; init; }
    public long? ExpectedCount { get; init; }
    public double MinConfidence { get; init; } = 0.5;
    public NormalizedRect? Region { get; init; }
    public bool Required { get; init; } = true;
}

/// <summary>算法、OCR、码制、行为或外部通讯统一传入 SOP 的事件。</summary>
public sealed record SopInputEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public string? SopRunId { get; init; }
    public string? Source { get; init; }
    public long FrameSequence { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public VisionEventPhase Phase { get; init; } = VisionEventPhase.Started;
    public string? SubjectId { get; init; }
    public string? ClassId { get; init; }
    public string? TextValue { get; init; }
    public string? CodeValue { get; init; }
    public long? Count { get; init; }
    public double Confidence { get; init; } = 1;
    public NormalizedRect? Box { get; init; }
    public string? RegionId { get; init; }
}

public sealed record SopStepSnapshot
{
    public required string StepId { get; init; }
    public required string Name { get; init; }
    public int Order { get; init; }
    public SopStepStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record SopSnapshot
{
    public SopRunStatus Status { get; init; } = SopRunStatus.Idle;
    public int CurrentStepOrder { get; init; }
    public string? CurrentStepName { get; init; }
    public int CompletedCount { get; init; }
    public int TotalCount { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<SopStepSnapshot> Steps { get; init; } = [];
}

public sealed record SopTransition
{
    public required SopSnapshot Snapshot { get; init; }
    public bool Changed { get; init; }
    public bool IsTerminal => Snapshot.Status is SopRunStatus.CompletedOk
        or SopRunStatus.NgTimeout
        or SopRunStatus.NgConditionFailed
        or SopRunStatus.NgWrongOrder
        or SopRunStatus.Interrupted
        or SopRunStatus.Aborted;
}

/// <summary>
/// 单产品 SOP 状态机。它只消费结构化事件，不读取 UI 文本，也不依赖具体算法插件。
/// </summary>
public sealed class SopStateMachine
{
    private readonly SopDefinition _definition;
    private readonly SopRunMode _runMode;
    private readonly Dictionary<string, SopStepRuntime> _steps;
    private readonly HashSet<string> _seenEvents = new(StringComparer.Ordinal);
    private int _currentIndex;
    private long _syntheticFrameSequence;
    private DateTimeOffset? _currentStepStartedAt;
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedDuration;
    private SopRunStatus _status = SopRunStatus.Idle;
    private string? _failureReason;
    private bool _started;

    private sealed class SopStepRuntime(SopStep definition)
    {
        public SopStep Definition { get; } = definition;
        public SopStepStatus Status { get; set; } = SopStepStatus.Pending;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? FailureReason { get; set; }
        public int StableFrames { get; set; }
        public long LastFrameSequence { get; set; } = -1;
    }

    public SopStateMachine(SopDefinition definition, SopRunMode runMode = SopRunMode.StrictOrder)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Steps.Count == 0)
        {
            throw new ArgumentException("SOP 至少需要一个步骤", nameof(definition));
        }

        _definition = definition with
        {
            Steps = definition.Steps.OrderBy(step => step.Order).ToArray(),
        };
        _runMode = runMode;
        _steps = _definition.Steps.ToDictionary(step => step.Id, step => new SopStepRuntime(step), StringComparer.Ordinal);
    }

    public SopSnapshot Snapshot => BuildSnapshot();

    /// <summary>当前工序 ID，供多模型运行器选择本帧应调用的算法会话。</summary>
    public string? CurrentStepId => CurrentRuntime?.Definition.Id;

    public SopTransition Start(DateTimeOffset timestamp, string? cycleId = null)
    {
        if (_started)
        {
            return Changed(false);
        }

        _started = true;
        _currentIndex = 0;
        _status = SopRunStatus.WaitingForStep;
        StartCurrentStep(timestamp);
        return Changed(true);
    }

    public SopTransition Apply(SopInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ApplyFrame([input], input.FrameSequence, input.OccurredAt);
    }

    /// <summary>
    /// 以完整帧作为一次观察窗口。一个步骤的所有必需条件必须在同一帧命中，
    /// 且帧序号连续，才会累计稳定帧；当前帧缺任一条件会清零稳定计数。
    /// </summary>
    public SopTransition ApplyFrame(
        IReadOnlyList<SopInputEvent> inputs,
        long frameSequence = -1,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var observedAt = timestamp
            ?? inputs.Select(input => input.OccurredAt).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        if (!_started)
        {
            Start(observedAt);
        }
        if (IsTerminal)
        {
            return Changed(false);
        }

        var freshInputs = inputs
            .Where(input => !string.IsNullOrWhiteSpace(input.EventId) && _seenEvents.Add(input.EventId))
            .ToArray();
        // 重复事件不能再次推进；空输入则是一个明确的“本帧没有命中”的观察帧。
        if (inputs.Count > 0 && freshInputs.Length == 0)
        {
            return Changed(false);
        }

        var sequence = frameSequence >= 0
            ? frameSequence
            : freshInputs.Select(input => input.FrameSequence).Where(value => value >= 0).DefaultIfEmpty(++_syntheticFrameSequence).Max();
        return EvaluateFrame(freshInputs, sequence, observedAt);
    }

    private SopTransition EvaluateFrame(
        IReadOnlyList<SopInputEvent> inputs,
        long frameSequence,
        DateTimeOffset timestamp)
    {
        var current = CurrentRuntime;
        if (current is null)
        {
            return Changed(false);
        }

        var futureIndex = FindFutureStepIndex(inputs);
        if (futureIndex >= 0 && current.Definition.EnforceOrder)
        {
            if (_runMode == SopRunMode.StrictOrder)
            {
                return Fail(SopRunStatus.NgWrongOrder, $"步骤「{current.Definition.Name}」尚未完成，检测到后续步骤事件");
            }

            if (_runMode == SopRunMode.ManualReview)
            {
                return RequestReview($"检测到后续步骤「{_definition.Steps[futureIndex].Name}」，请人工确认");
            }

            // AllowSkip：把中间步骤标记为跳过，并继续用本帧事件尝试推进目标步骤。
            for (var index = _currentIndex; index < futureIndex; index++)
            {
                var skipped = _steps[_definition.Steps[index].Id];
                skipped.Status = SopStepStatus.Skipped;
                skipped.CompletedAt = timestamp;
            }
            _currentIndex = futureIndex;
            StartCurrentStep(timestamp);
            current = CurrentRuntime;
            if (current is null)
            {
                return Changed(true);
            }
        }

        var required = current.Definition.Conditions.Where(condition => condition.Required).ToArray();
        var conditionsSatisfied = required.Length == 0
            || required.All(condition => inputs.Any(input => Matches(condition, input)));

        if (!conditionsSatisfied)
        {
            current.StableFrames = 0;
            current.LastFrameSequence = frameSequence;
            _status = SopRunStatus.WaitingForStep;
            return Changed(true);
        }

        var isNewFrame = current.LastFrameSequence < 0 || frameSequence > current.LastFrameSequence;
        if (isNewFrame)
        {
            current.StableFrames = current.LastFrameSequence >= 0 && frameSequence == current.LastFrameSequence + 1
                ? current.StableFrames + 1
                : 1;
            current.LastFrameSequence = frameSequence;
        }
        _status = SopRunStatus.Stabilizing;

        if (current.StableFrames >= Math.Max(1, current.Definition.MinimumStableFrames))
        {
            CompleteCurrent(timestamp);
            return Changed(true);
        }

        return Changed(true);
    }

    public SopTransition AdvanceTime(DateTimeOffset timestamp)
    {
        if (!_started || IsTerminal || CurrentRuntime is not { } current || _currentStepStartedAt is not { } startedAt)
        {
            return Changed(false);
        }

        // 暂停期间不消耗步骤预算；Resume 会把暂停区间累计到 _pausedDuration。
        if (_pausedAt is not null)
        {
            return Changed(false);
        }

        if (current.Definition.TimeoutSeconds > 0
            && timestamp - startedAt - _pausedDuration >= TimeSpan.FromSeconds(current.Definition.TimeoutSeconds))
        {
            return Fail(SopRunStatus.NgTimeout, $"步骤「{current.Definition.Name}」超时");
        }

        return Changed(false);
    }

    /// <summary>暂停当前步骤的有效计时。重复暂停幂等。</summary>
    public void Pause(DateTimeOffset timestamp)
    {
        if (_started && !IsTerminal && _pausedAt is null)
        {
            _pausedAt = timestamp;
        }
    }

    /// <summary>恢复当前步骤的有效计时。重复恢复幂等。</summary>
    public void Resume(DateTimeOffset timestamp)
    {
        if (_pausedAt is { } pausedAt)
        {
            _pausedDuration += timestamp >= pausedAt ? timestamp - pausedAt : TimeSpan.Zero;
            _pausedAt = null;
        }
    }

    public SopTransition RequestReview(string reason)
    {
        if (IsTerminal)
        {
            return Changed(false);
        }
        _status = SopRunStatus.ReviewRequired;
        _failureReason = string.IsNullOrWhiteSpace(reason) ? "需要人工复核" : reason;
        if (CurrentRuntime is { } current)
        {
            current.Status = SopStepStatus.ReviewRequired;
            current.FailureReason = _failureReason;
        }
        return Changed(true);
    }

    public SopTransition Abort(string reason, bool interrupted = false)
    {
        if (IsTerminal)
        {
            return Changed(false);
        }
        _status = interrupted ? SopRunStatus.Interrupted : SopRunStatus.Aborted;
        _failureReason = string.IsNullOrWhiteSpace(reason) ? "检测周期已中止" : reason;
        return Changed(true);
    }

    public SopTransition Reset()
    {
        foreach (var step in _steps.Values)
        {
            step.Status = SopStepStatus.Pending;
            step.StartedAt = null;
            step.CompletedAt = null;
            step.FailureReason = null;
            step.StableFrames = 0;
            step.LastFrameSequence = -1;
        }
        _seenEvents.Clear();
        _syntheticFrameSequence = 0;
        _currentIndex = 0;
        _currentStepStartedAt = null;
        _pausedAt = null;
        _pausedDuration = TimeSpan.Zero;
        _failureReason = null;
        _started = false;
        _status = SopRunStatus.Idle;
        return Changed(true);
    }

    private bool IsTerminal => _status is SopRunStatus.CompletedOk
        or SopRunStatus.NgTimeout
        or SopRunStatus.NgConditionFailed
        or SopRunStatus.NgWrongOrder
        or SopRunStatus.Interrupted
        or SopRunStatus.Aborted;

    private SopStepRuntime? CurrentRuntime => _currentIndex >= 0 && _currentIndex < _definition.Steps.Count
        ? _steps[_definition.Steps[_currentIndex].Id]
        : null;

    private void StartCurrentStep(DateTimeOffset timestamp)
    {
        if (CurrentRuntime is not { } current)
        {
            _status = SopRunStatus.CompletedOk;
            return;
        }
        current.Status = SopStepStatus.InProgress;
        current.StartedAt = timestamp;
        current.StableFrames = 0;
        current.LastFrameSequence = -1;
        _currentStepStartedAt = timestamp;
        _pausedAt = null;
        _pausedDuration = TimeSpan.Zero;
        _status = SopRunStatus.WaitingForStep;
    }

    private void CompleteCurrent(DateTimeOffset timestamp)
    {
        if (CurrentRuntime is not { } current)
        {
            _status = SopRunStatus.CompletedOk;
            return;
        }
        current.Status = SopStepStatus.Completed;
        current.CompletedAt = timestamp;
        _currentIndex++;
        if (_currentIndex >= _definition.Steps.Count)
        {
            _currentStepStartedAt = null;
            _status = SopRunStatus.CompletedOk;
            return;
        }
        _status = SopRunStatus.StepCompleted;
        StartCurrentStep(timestamp);
    }

    private SopTransition Fail(SopRunStatus status, string reason)
    {
        _status = status;
        _failureReason = reason;
        if (CurrentRuntime is { } current)
        {
            current.Status = SopStepStatus.Failed;
            current.FailureReason = reason;
        }
        return Changed(true);
    }

    private int FindFutureStepIndex(IReadOnlyList<SopInputEvent> inputs)
    {
        for (var index = _currentIndex + 1; index < _definition.Steps.Count; index++)
        {
            if (_definition.Steps[index].Conditions.Any(condition => inputs.Any(input => Matches(condition, input))))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool Matches(SopCondition condition, SopInputEvent input)
    {
        if (input.Confidence < condition.MinConfidence)
        {
            return false;
        }
        if (condition.Region is { } region && (input.Box is not { } box || !Overlaps(region, box)))
        {
            return false;
        }

        return condition.Kind switch
        {
            SopConditionKind.ObjectPresent => EventIs(input, condition.EventType, "object.present")
                && Same(condition.ClassId, input.ClassId),
            SopConditionKind.ObjectAbsent => EventIs(input, condition.EventType, "object.absent")
                && Same(condition.ClassId, input.ClassId),
            SopConditionKind.ObjectCount => EventIs(input, condition.EventType, "object.count")
                && Same(condition.ClassId, input.ClassId)
                && condition.ExpectedCount is { } expected && input.Count == expected,
            SopConditionKind.ObjectInRegion => EventIs(input, condition.EventType, "object.present")
                && Same(condition.ClassId, input.ClassId) && condition.Region is not null,
            SopConditionKind.ObjectStable => EventIs(input, condition.EventType, "object.stable")
                && Same(condition.ClassId, input.ClassId),
            SopConditionKind.TextEquals => EventIs(input, condition.EventType, "text.recognized")
                && string.Equals(condition.TextValue, input.TextValue, StringComparison.OrdinalIgnoreCase),
            SopConditionKind.CodeEquals => EventIs(input, condition.EventType, "code.recognized")
                && string.Equals(condition.CodeValue, input.CodeValue, StringComparison.OrdinalIgnoreCase),
            SopConditionKind.BehaviorStarted => input.Phase == VisionEventPhase.Started
                && EventIs(input, condition.EventType, "behavior.started"),
            SopConditionKind.BehaviorCompleted => input.Phase == VisionEventPhase.Completed
                && EventIs(input, condition.EventType, "behavior.completed"),
            SopConditionKind.RegionChanged => EventIs(input, condition.EventType, "region.changed"),
            SopConditionKind.CustomEvent => EventIs(input, condition.EventType, null),
            _ => false,
        };
    }

    private static bool EventIs(SopInputEvent input, string? expected, string? fallback)
        => string.Equals(input.EventType, expected ?? fallback, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? expected, string? actual)
        => string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(NormalizedRect a, NormalizedRect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X
            && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private SopTransition Changed(bool changed) => new() { Snapshot = BuildSnapshot(), Changed = changed };

    private SopSnapshot BuildSnapshot()
    {
        var steps = _definition.Steps.Select(step =>
        {
            var state = _steps[step.Id];
            return new SopStepSnapshot
            {
                StepId = step.Id,
                Name = step.Name,
                Order = step.Order,
                Status = state.Status,
                StartedAt = state.StartedAt,
                CompletedAt = state.CompletedAt,
                FailureReason = state.FailureReason,
            };
        }).ToArray();
        var current = CurrentRuntime;
        return new SopSnapshot
        {
            Status = _status,
            CurrentStepOrder = current?.Definition.Order ?? 0,
            CurrentStepName = current?.Definition.Name,
            CompletedCount = steps.Count(step => step.Status == SopStepStatus.Completed),
            TotalCount = steps.Length,
            FailureReason = _failureReason,
            Steps = steps,
        };
    }
}
