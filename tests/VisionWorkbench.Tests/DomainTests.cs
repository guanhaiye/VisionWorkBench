using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>规则引擎 RUL-001~005、ROI 边界 CNT-S-006/007、计数累计与纠错审计 CNT-S-010。</summary>
public sealed class DomainTests
{
    private static AlgorithmOutput Output(params (string classId, double x, double y, double conf)[] detections)
    {
        return new AlgorithmOutput
        {
            OutputId = "o1",
            InputId = "i1",
            Detections = [.. detections.Select(d => new DetectionResult
            {
                ClassId = d.classId,
                ClassName = d.classId,
                Box = new NormalizedRect { X = d.x, Y = d.y, Width = 0.1, Height = 0.1 },
                Confidence = d.conf,
            })],
        };
    }

    private static readonly (string, double, double, double)[] ThreeObjects =
        [("obj", 0.2, 0.2, 0.9), ("obj", 0.5, 0.2, 0.85), ("obj", 0.8, 0.2, 0.95)];

    // ---- RUL-001/002/003 数量规则 ----

    [Fact]
    public void CountEquals_Passes_When_Match()
    {
        var decision = RuleEngine.Evaluate(Output(ThreeObjects),
        [
            new InspectionRule { RuleId = "r1", Kind = RuleKind.CountEquals, ExpectedCount = 3 },
        ]);
        Assert.Equal(DecisionStatus.Ok, decision.Status);
    }

    [Fact]
    public void CountEquals_Fails_To_Ng_When_Mismatch()
    {
        var decision = RuleEngine.Evaluate(Output(ThreeObjects),
        [
            new InspectionRule { RuleId = "r1", Kind = RuleKind.CountEquals, ExpectedCount = 5 },
        ]);
        Assert.Equal(DecisionStatus.Ng, decision.Status);
        Assert.Contains(decision.Failed, f => f.RuleId == "r1");
        Assert.Contains("3", decision.Summary);
    }

    [Fact]
    public void CountAtLeast_And_AtMost_Boundaries()
    {
        var rules = new[]
        {
            new InspectionRule { RuleId = "min", Kind = RuleKind.CountAtLeast, ExpectedCount = 3 },
            new InspectionRule { RuleId = "max", Kind = RuleKind.CountAtMost, ExpectedCount = 3 },
        };
        Assert.Equal(DecisionStatus.Ok, RuleEngine.Evaluate(Output(ThreeObjects), rules).Status);

        var strict = new[]
        {
            new InspectionRule { RuleId = "min", Kind = RuleKind.CountAtLeast, ExpectedCount = 4 },
        };
        Assert.Equal(DecisionStatus.Ng, RuleEngine.Evaluate(Output(ThreeObjects), strict).Status);
    }

    [Fact]
    public void CountRule_Filtered_By_ClassId()
    {
        var output = Output([("obj", 0.2, 0.2, 0.9), ("defect", 0.5, 0.5, 0.9), ("defect", 0.7, 0.5, 0.9)]);
        var decision = RuleEngine.Evaluate(output,
        [
            new InspectionRule { RuleId = "defects", Kind = RuleKind.CountAtMost, ClassId = "defect", ExpectedCount = 1 },
        ]);
        Assert.Equal(DecisionStatus.Ng, decision.Status);
    }

    // ---- RUL-004/005 类别与置信度 ----

    [Fact]
    public void ClassRequired_And_Forbidden()
    {
        var output = Output([("obj", 0.2, 0.2, 0.9), ("cap", 0.5, 0.5, 0.9)]);
        var ok = RuleEngine.Evaluate(output,
        [
            new InspectionRule { RuleId = "need-cap", Kind = RuleKind.ClassRequired, ClassId = "cap" },
            new InspectionRule { RuleId = "no-glue", Kind = RuleKind.ClassForbidden, ClassId = "glue" },
        ]);
        Assert.Equal(DecisionStatus.Ok, ok.Status);

        var missing = RuleEngine.Evaluate(Output([("obj", 0.2, 0.2, 0.9)]),
        [
            new InspectionRule { RuleId = "need-cap", Kind = RuleKind.ClassRequired, ClassId = "cap" },
        ]);
        Assert.Equal(DecisionStatus.Ng, missing.Status);
    }

    [Fact]
    public void LowConfidence_Becomes_ReviewRequired_Not_Ng()
    {
        var lowConf = Output([("obj", 0.2, 0.2, 0.45)]);
        var decision = RuleEngine.Evaluate(lowConf,
        [
            new InspectionRule { RuleId = "conf", Kind = RuleKind.LowConfidence, MinConfidence = 0.6 },
        ]);
        Assert.Equal(DecisionStatus.ReviewRequired, decision.Status);
    }

    [Fact]
    public void Ng_Takes_Priority_Over_Review()
    {
        var decision = RuleEngine.Evaluate(Output(ThreeObjects),
        [
            new InspectionRule { RuleId = "conf", Kind = RuleKind.LowConfidence, MinConfidence = 0.99 },
            new InspectionRule { RuleId = "count", Kind = RuleKind.CountEquals, ExpectedCount = 999 },
        ]);
        Assert.Equal(DecisionStatus.Ng, decision.Status);
    }

    [Fact]
    public void Disabled_Rule_Is_Skipped()
    {
        var decision = RuleEngine.Evaluate(Output(ThreeObjects),
        [
            new InspectionRule { RuleId = "r1", Kind = RuleKind.CountEquals, ExpectedCount = 100, Enabled = false },
        ]);
        Assert.Equal(DecisionStatus.Ok, decision.Status);
        Assert.Empty(decision.Outcomes);
    }

    // ---- CNT-S-006/007 ROI 边界 ----

    [Fact]
    public void Roi_CenterInside_Excludes_Border_Boxes()
    {
        var output = Output(
            ("obj", 0.10, 0.5, 0.9),   // 中心 (0.15, 0.55) 在 ROI 内
            ("obj", 0.00, 0.5, 0.9),   // 中心 (0.05, 0.55) 在 ROI 边缘内
            ("obj", 0.35, 0.5, 0.9));  // 中心 (0.40, 0.55) 在 ROI 外
        var roi = new NormalizedRect { X = 0.05, Y = 0.0, Width = 0.2, Height = 1.0 };
        var filtered = RoiFilter.Apply(output.Detections, roi, RoiBoundaryPolicy.CenterInside);
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Roi_FullInside_Stricter_Than_Center()
    {
        // 框 X=0.02..0.12 中心在 ROI 但框出界
        var detections = new[]
        {
            new DetectionResult
            {
                ClassId = "obj",
                Box = new NormalizedRect { X = 0.02, Y = 0.4, Width = 0.1, Height = 0.1 },
                Confidence = 0.9,
            },
        };
        var roi = new NormalizedRect { X = 0.05, Y = 0.0, Width = 0.2, Height = 1.0 };
        Assert.Single(RoiFilter.Apply(detections, roi, RoiBoundaryPolicy.CenterInside));
        Assert.Empty(RoiFilter.Apply(detections, roi, RoiBoundaryPolicy.FullInside));
        Assert.Single(RoiFilter.Apply(detections, roi, RoiBoundaryPolicy.AnyOverlap));
    }

    [Fact]
    public void Roi_Rules_Use_Filtered_Detections()
    {
        var output = Output(("obj", 0.10, 0.5, 0.9), ("obj", 0.60, 0.5, 0.9));
        var roi = new NormalizedRect { X = 0.0, Y = 0.0, Width = 0.3, Height = 1.0 };
        var decision = RuleEngine.Evaluate(output,
        [
            new InspectionRule { RuleId = "roi-count", Kind = RuleKind.CountEquals, ExpectedCount = 1 },
        ], roi, RoiBoundaryPolicy.CenterInside);
        Assert.Equal(DecisionStatus.Ok, decision.Status);
    }

    // ---- CNT-S-010 计数累计与纠错 ----

    [Fact]
    public void Roi_Output_Filters_Detections_And_Updates_Count()
    {
        var output = Output(("obj", 0.10, 0.5, 0.9), ("obj", 0.60, 0.5, 0.9)) with
        {
            Metrics = [new MetricResult { Name = "count", Value = 2 }],
        };
        var roi = new NormalizedRect { X = 0.0, Y = 0.0, Width = 0.3, Height = 1.0 };

        var filtered = RoiFilter.ApplyToOutput(output, roi, RoiBoundaryPolicy.CenterInside);

        Assert.Single(filtered.Detections);
        Assert.Equal(1, filtered.GetCount());
    }

    [Fact]
    public void DefectSeverityThreshold_Fails_When_Area_Or_Severity_Exceeds()
    {
        var output = new AlgorithmOutput
        {
            OutputId = "o1",
            InputId = "i1",
            Detections =
            [
                new DetectionResult
                {
                    ClassId = "scratch",
                    Severity = "critical",
                    AreaRatio = 0.01,
                    Box = new NormalizedRect { X = 0.2, Y = 0.2, Width = 0.1, Height = 0.1 },
                },
            ],
        };
        var decision = RuleEngine.Evaluate(output,
        [new InspectionRule
        {
            RuleId = "severity",
            Kind = RuleKind.DefectSeverityThreshold,
            ClassId = "scratch",
            Threshold = 2,
        }]);

        Assert.Equal(DecisionStatus.Ng, decision.Status);
    }

    [Fact]
    public void EventDurationThreshold_Downgrades_To_Review()
    {
        var start = DateTimeOffset.UtcNow;
        var output = new AlgorithmOutput
        {
            OutputId = "o1",
            InputId = "i1",
            Events =
            [
                new VisionEvent
                {
                    EventId = "e1",
                    EventType = "blocked",
                    StartedAt = start,
                    EndedAt = start.AddMilliseconds(250),
                },
            ],
        };
        var decision = RuleEngine.Evaluate(output,
        [new InspectionRule
        {
            RuleId = "duration",
            Kind = RuleKind.EventDurationThreshold,
            ClassId = "blocked",
            DurationMs = 100,
        }]);

        Assert.Equal(DecisionStatus.ReviewRequired, decision.Status);
    }

    [Fact]
    public void CounterState_Accumulates_Events()
    {
        var state = new CounterState("c1");
        state.Apply(new CountingEvent { EventId = "e1", CounterId = "c1", Type = CountingEventType.Appeared, Delta = 1 });
        state.Apply(new CountingEvent { EventId = "e2", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "forward", Delta = 1 });
        state.Apply(new CountingEvent { EventId = "e3", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "reverse", Delta = 1 });
        Assert.Equal(3, state.CurrentTotal);
        Assert.Equal(1, state.ForwardTotal);
        Assert.Equal(1, state.ReverseTotal);
    }

    [Fact]
    public void CounterReset_Zeroes_All_Totals()
    {
        var state = new CounterState("c1");
        state.Apply(new CountingEvent { EventId = "e1", CounterId = "c1", Type = CountingEventType.Appeared, Delta = 1 });
        state.Apply(new CountingEvent { EventId = "e2", CounterId = "c1", Type = CountingEventType.CounterReset });
        Assert.Equal(0, state.CurrentTotal);
    }

    [Fact]
    public void Adjust_Requires_Reason_And_Audits_Before_After()
    {
        var service = new CountingService("c1");
        Assert.Throws<ArgumentException>(() => service.Adjust(1, "", "op"));

        var adjustment = service.Adjust(-1, "漏检一个，人工扣除", "张工");
        Assert.Equal(0, adjustment.Before);
        Assert.Equal(-1, adjustment.After);
        Assert.Equal(CountingEventType.Corrected, adjustment.Event.Type);
        Assert.Equal("张工", adjustment.Operator);
        Assert.Equal(-1, service.State.CurrentTotal);
    }

    [Fact]
    public void Concurrent_Adjustments_Audit_Only_Their_Own_Change()
    {
        const int count = 10000;
        var service = new CountingService("c1");
        var adjustments = new CountingAdjustment[count];

        Parallel.For(0, count, index =>
            adjustments[index] = service.Adjust(1, "并发修正", "operator"));

        Assert.Equal(count, service.State.CurrentTotal);
        Assert.All(adjustments, change => Assert.Equal(1, change.After - change.Before));
        Assert.Equal(count, adjustments.Select(change => change.Before).Distinct().Count());
    }

    [Fact]
    public void ApplyOutput_Feeds_Counter_From_Algorithm_Events()
    {
        var service = new CountingService("c1");
        var output = new AlgorithmOutput
        {
            OutputId = "o1",
            InputId = "i1",
            CountingEvents =
            [
                new CountingEvent { EventId = "e1", CounterId = "c1", Type = CountingEventType.Accepted, Delta = 1 },
                new CountingEvent { EventId = "e2", CounterId = "c1", Type = CountingEventType.Accepted, Delta = 1 },
            ],
        };
        service.ApplyOutput(output);
        Assert.Equal(2, service.State.AcceptedTotal);
        Assert.Equal(2, service.State.CurrentTotal);
    }

    [Fact]
    public void ApplyOutput_SnapshotFallback_Accumulates_Object_Count_When_Events_Are_Empty()
    {
        var service = new CountingService("c1");
        var output = new AlgorithmOutput
        {
            OutputId = "o1",
            InputId = "i1",
            Sequence = 7,
            Detections =
            [
                new DetectionResult { ClassId = "part", ClassName = "零件", Confidence = 0.9, Box = new() },
                new DetectionResult { ClassId = "part", ClassName = "零件", Confidence = 0.8, Box = new() },
            ],
        };

        var events = service.ApplyOutput(output, snapshotFallback: true);

        Assert.Single(events);
        Assert.Equal(CountingEventType.Appeared, events[0].Type);
        Assert.Equal(2, events[0].Delta);
        Assert.Equal(2, service.State.CurrentTotal);
    }

    // ---- CNT-L-014 事件重放恢复 ----

    [Fact]
    public void RestoreFrom_Replays_CrossedLine_Corrected_Reset()
    {
        var service = new CountingService("c1");
        service.RestoreFrom(
        [
            new CountingEvent { EventId = "e1", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "forward", Delta = 1 },
            new CountingEvent { EventId = "e2", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "forward", Delta = 1 },
            new CountingEvent { EventId = "c1", CounterId = "c1", Type = CountingEventType.Corrected, Delta = 1 },
            new CountingEvent { EventId = "e3", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "reverse", Delta = 1 },
        ]);
        Assert.Equal(4, service.State.CurrentTotal);
        Assert.Equal(2, service.State.ForwardTotal);
        Assert.Equal(1, service.State.ReverseTotal);
        Assert.Equal(1, service.State.CorrectedTotal);

        // 重放含 CounterReset 的完整历史：reset 后的清零语义天然正确
        var replay = new CountingService("c1");
        replay.RestoreFrom(
        [
            new CountingEvent { EventId = "e1", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "forward", Delta = 1 },
            new CountingEvent { EventId = "e2", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "reverse", Delta = 1 },
            new CountingEvent { EventId = "r1", CounterId = "c1", Type = CountingEventType.CounterReset },
            new CountingEvent { EventId = "e3", CounterId = "c1", Type = CountingEventType.CrossedLine, Direction = "forward", Delta = 1 },
        ]);
        Assert.Equal(1, replay.State.CurrentTotal);
        Assert.Equal(1, replay.State.ForwardTotal); // reset 清零后重新累计
        Assert.Equal(0, replay.State.ReverseTotal);
        Assert.Equal(0, replay.State.CorrectedTotal);
    }
}
