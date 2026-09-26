using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Domain;

/// <summary>可变计数累计器：事件累计产生总数，不允许只存总数（文档 §16.7/§16.8）。</summary>
public sealed class CounterState
{
    private readonly object _lock = new();

    public string CounterId { get; }
    public long Total { get; private set; }
    public long ForwardTotal { get; private set; }
    public long ReverseTotal { get; private set; }
    public long AcceptedTotal { get; private set; }
    public long RejectedTotal { get; private set; }
    public long UncertainTotal { get; private set; }
    public long CorrectedTotal { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public CounterState(string counterId) => CounterId = counterId;

    /// <summary>应用一条计数事件并更新累计值。</summary>
    public void Apply(CountingEvent evt) => ApplyWithTotals(evt);

    internal (long Before, long After) ApplyWithTotals(CountingEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_lock)
        {
            var before = Total;
            switch (evt.Type)
            {
                case CountingEventType.CounterReset:
                    Total = ForwardTotal = ReverseTotal = 0;
                    AcceptedTotal = RejectedTotal = UncertainTotal = CorrectedTotal = 0;
                    break;
                case CountingEventType.Accepted:
                    AcceptedTotal += Math.Max(0, evt.Delta);
                    Total += evt.Delta;
                    break;
                case CountingEventType.Rejected:
                    RejectedTotal += Math.Max(0, evt.Delta);
                    Total += evt.Delta;
                    break;
                case CountingEventType.Uncertain:
                    UncertainTotal += Math.Max(0, evt.Delta);
                    break;
                case CountingEventType.Corrected:
                    CorrectedTotal++;
                    Total += evt.Delta;
                    break;
                case CountingEventType.CrossedLine:
                    if (string.Equals(evt.Direction, "forward", StringComparison.OrdinalIgnoreCase))
                    {
                        ForwardTotal++;
                    }
                    else if (string.Equals(evt.Direction, "reverse", StringComparison.OrdinalIgnoreCase))
                    {
                        ReverseTotal++;
                    }
                    Total += evt.Delta != 0 ? evt.Delta : 1;
                    break;
                default:
                    // Appeared / EnteredZone / ExitedZone
                    Total += evt.Delta != 0 ? evt.Delta : 1;
                    break;
            }
            UpdatedAt = DateTimeOffset.UtcNow;
            return (before, Total);
        }
    }

    public long CurrentTotal { get { lock (_lock) { return Total; } } }
}

/// <summary>人工修正审计（文档 §16.8：修改前后值、原因、时间、操作者）。</summary>
public sealed record CountingAdjustment
{
    public required CountingEvent Event { get; init; }
    public long Before { get; init; }
    public long After { get; init; }
    public required string Reason { get; init; }
    public required string Operator { get; init; }
    public DateTimeOffset CorrectedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>计数服务：事件应用 + 手动加减一/清零（带审计）。</summary>
public sealed class CountingService
{
    private readonly CounterState _state;
    private long _eventSeq;

    public CountingService(string counterId) => _state = new CounterState(counterId);

    public CounterState State => _state;

    /// <summary>
    /// 应用算法产生的计数事件。快照模式下，普通检测模型通常只返回检测结果/数量指标，
    /// 不会额外生成 CountingEvents，因此在没有事件时按本帧对象数量补一条 Appeared 事件。
    /// </summary>
    public IReadOnlyList<CountingEvent> ApplyOutput(AlgorithmOutput output, bool snapshotFallback = false)
    {
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<CountingEvent> events = output.CountingEvents;
        if (snapshotFallback && events.Count == 0)
        {
            var count = Math.Max(0, output.GetCount());
            if (count > 0)
            {
                events =
                [
                    new CountingEvent
                    {
                        EventId = $"snapshot-{output.OutputId}",
                        CounterId = _state.CounterId,
                        Type = CountingEventType.Appeared,
                        Delta = count,
                        Confidence = 1.0,
                        FrameSequence = output.Sequence,
                        OccurredAt = output.Timestamp,
                    },
                ];
            }
        }

        foreach (var evt in events)
        {
            _state.Apply(evt);
        }
        return events;
    }

    /// <summary>
    /// 按事件顺序重放恢复累计（CNT-L-014）：历史/纠错事件流 → 累计值。
    /// 用于会话重建或只读展示；流内 CounterReset 自然清零，Corrected 按差值重放。
    /// </summary>
    public void RestoreFrom(IEnumerable<CountingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var evt in events)
        {
            _state.Apply(evt);
        }
    }

    /// <summary>手动修正（CNT-S-010）：delta 可为 ±1、±n；reason 必填。</summary>
    public CountingAdjustment Adjust(long delta, string reason, string @operator, long frameSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("人工修正必须填写原因（CNT-S-010）", nameof(reason));
        }
        var evt = new CountingEvent
        {
            EventId = $"corr-{Guid.NewGuid():N}",
            CounterId = _state.CounterId,
            Type = CountingEventType.Corrected,
            Delta = delta,
            Confidence = 1.0,
            FrameSequence = frameSequence,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var (before, after) = _state.ApplyWithTotals(evt);
        return new CountingAdjustment
        {
            Event = evt,
            Before = before,
            After = after,
            Reason = reason,
            Operator = @operator,
        };
    }

    /// <summary>手动清零（CNT-S-010）：产生 CounterReset 事件并审计。</summary>
    public CountingAdjustment Reset(string reason, string @operator, long frameSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("清零必须填写原因（CNT-S-010）", nameof(reason));
        }
        var evt = new CountingEvent
        {
            EventId = $"reset-{Guid.NewGuid():N}",
            CounterId = _state.CounterId,
            Type = CountingEventType.CounterReset,
            Delta = 0,
            Confidence = 1.0,
            FrameSequence = frameSequence,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var (before, after) = _state.ApplyWithTotals(evt);
        return new CountingAdjustment
        {
            Event = evt,
            Before = before,
            After = after,
            Reason = reason,
            Operator = @operator,
        };
    }

    public long NextEventSeq() => Interlocked.Increment(ref _eventSeq);
}
