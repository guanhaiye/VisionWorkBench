using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Application;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class SopWorkflowTests
{
    private static SopDefinition Definition(int stableFrames = 2) => new()
    {
        Id = "inline:test-sop",
        Code = "TEST-SOP",
        Name = "测试 SOP",
        Steps =
        [
            Step("下盖", "bottom", 1, stableFrames),
            Step("安装堵头", "plug", 2, 1),
        ],
    };

    private static SopStep Step(string name, string classId, int order, int stableFrames) => new()
    {
        Id = $"step-{order}",
        Code = $"step-{order}",
        Name = name,
        Order = order,
        MinimumStableFrames = stableFrames,
        TimeoutSeconds = 10,
        Conditions =
        [
            new SopCondition
            {
                Id = $"step-{order}-condition",
                Kind = SopConditionKind.ObjectPresent,
                EventType = "object.present",
                ClassId = classId,
                MinConfidence = 0.5,
            },
        ],
    };

    private static SopInputEvent Object(string id, long frame, string classId) => new()
    {
        EventId = id,
        EventType = "object.present",
        FrameSequence = frame,
        OccurredAt = DateTimeOffset.UtcNow.AddSeconds(frame),
        ClassId = classId,
        Confidence = 0.95,
    };

    private static SopDefinition MultiConditionDefinition() => new()
    {
        Id = "inline:multi-condition",
        Code = "MULTI",
        Name = "多条件测试",
        Steps =
        [
            new SopStep
            {
                Id = "multi-step",
                Code = "multi-step",
                Name = "目标和按钮事件",
                Order = 1,
                MinimumStableFrames = 2,
                Conditions =
                [
                    new SopCondition
                    {
                        Id = "object-condition",
                        Kind = SopConditionKind.ObjectPresent,
                        EventType = "object.present",
                        ClassId = "part",
                    },
                    new SopCondition
                    {
                        Id = "button-condition",
                        Kind = SopConditionKind.CustomEvent,
                        EventType = "button.pressed",
                    },
                ],
            },
        ],
    };

    [Fact]
    public void StrictOrder_Completes_Only_After_Stable_Frames()
    {
        var machine = new SopStateMachine(Definition());
        machine.Start(DateTimeOffset.UtcNow);

        var first = machine.Apply(Object("e1", 1, "bottom"));
        Assert.Equal(SopRunStatus.Stabilizing, first.Snapshot.Status);
        Assert.Equal(0, first.Snapshot.CompletedCount);

        var second = machine.Apply(Object("e2", 2, "bottom"));
        Assert.Equal(SopStepStatus.Completed, second.Snapshot.Steps[0].Status);
        Assert.Equal("安装堵头", second.Snapshot.CurrentStepName);
        Assert.Equal(SopRunStatus.WaitingForStep, second.Snapshot.Status);

        var finished = machine.Apply(Object("e3", 3, "plug"));
        Assert.Equal(SopRunStatus.CompletedOk, finished.Snapshot.Status);
        Assert.Equal(2, finished.Snapshot.CompletedCount);
    }

    [Fact]
    public void DuplicateEvent_Is_Idempotent()
    {
        var machine = new SopStateMachine(Definition());
        machine.Start(DateTimeOffset.UtcNow);
        machine.Apply(Object("same", 1, "bottom"));
        machine.Apply(Object("same", 1, "bottom"));

        Assert.NotEqual(SopStepStatus.Completed, machine.Snapshot.Steps[0].Status);
        Assert.Equal(0, machine.Snapshot.CompletedCount);
    }

    [Fact]
    public void StrictOrder_Rejects_FutureStep_Event()
    {
        var machine = new SopStateMachine(Definition(stableFrames: 1));
        machine.Start(DateTimeOffset.UtcNow);

        var result = machine.Apply(Object("future", 1, "plug"));

        Assert.Equal(SopRunStatus.NgWrongOrder, result.Snapshot.Status);
        Assert.Contains("下盖", result.Snapshot.FailureReason);
    }

    [Fact]
    public void Timeout_Fails_CurrentStep()
    {
        var started = DateTimeOffset.UtcNow;
        var machine = new SopStateMachine(Definition());
        machine.Start(started);

        var result = machine.AdvanceTime(started.AddSeconds(11));

        Assert.Equal(SopRunStatus.NgTimeout, result.Snapshot.Status);
        Assert.Equal(SopStepStatus.Failed, result.Snapshot.Steps[0].Status);
    }

    [Fact]
    public void Missing_Frame_Breaks_Stable_Sequence()
    {
        var machine = new SopStateMachine(Definition(stableFrames: 3));
        machine.Start(DateTimeOffset.UtcNow);

        machine.ApplyFrame([Object("hit-1", 1, "bottom")], 1);
        machine.ApplyFrame([], 2);
        machine.ApplyFrame([Object("hit-3", 3, "bottom")], 3);

        Assert.Equal(0, machine.Snapshot.CompletedCount);
        machine.ApplyFrame([Object("hit-4", 4, "bottom")], 4);
        machine.ApplyFrame([Object("hit-5", 5, "bottom")], 5);
        Assert.Equal(1, machine.Snapshot.CompletedCount);
    }

    [Fact]
    public void Required_Conditions_Must_Match_In_The_Same_Frame()
    {
        var machine = new SopStateMachine(MultiConditionDefinition());
        machine.Start(DateTimeOffset.UtcNow);

        machine.ApplyFrame([Object("object-1", 1, "part")], 1);
        machine.ApplyFrame([new SopInputEvent
        {
            EventId = "button-2",
            EventType = "button.pressed",
            FrameSequence = 2,
            Confidence = 1,
        }], 2);
        Assert.Equal(0, machine.Snapshot.CompletedCount);

        var bothFrame = machine.ApplyFrame(
        [
            Object("object-3", 3, "part"),
            new SopInputEvent { EventId = "button-3", EventType = "button.pressed", FrameSequence = 3 },
        ], 3);
        Assert.Equal(0, bothFrame.Snapshot.CompletedCount);

        var completed = machine.ApplyFrame(
        [
            Object("object-4", 4, "part"),
            new SopInputEvent { EventId = "button-4", EventType = "button.pressed", FrameSequence = 4 },
        ], 4);
        Assert.Equal(SopRunStatus.CompletedOk, completed.Snapshot.Status);
    }

    [Fact]
    public void Python_PostProcess_Event_Is_Converted_To_Sop_Input()
    {
        var output = new AlgorithmOutput
        {
            OutputId = "python-output",
            InputId = "input",
            Sequence = 8,
            Events =
            [
                new VisionEvent
                {
                    EventId = "python-button",
                    EventType = "button.pressed",
                    Confidence = 1,
                    TextValue = "OK",
                },
            ],
        };

        var inputs = DetectionRunService.BuildSopInputs(output, "cycle-python");

        var input = Assert.Single(inputs);
        Assert.Equal("button.pressed", input.EventType);
        Assert.Equal("python-button", input.EventId.Split(':').Last());
        Assert.Equal("cycle-python", input.SopRunId);
        Assert.Equal("OK", input.TextValue);
    }

    [Fact]
    public void Final_Decision_Requires_Rules_And_Sop()
    {
        var visualOk = new DecisionResult
        {
            Status = DecisionStatus.Ok,
            Outcomes =
            [new RuleOutcome { RuleId = "visual", RuleKind = "test", Passed = true, Message = "规则通过" }],
        };
        var waiting = new SopSnapshot { Status = SopRunStatus.WaitingForStep, TotalCount = 1 };
        var failed = new SopSnapshot { Status = SopRunStatus.NgWrongOrder, TotalCount = 1, FailureReason = "错序" };
        var completed = new SopSnapshot { Status = SopRunStatus.CompletedOk, TotalCount = 1, CompletedCount = 1 };

        Assert.Equal(DecisionStatus.Processing, DetectionRunService.CombineDecision(visualOk, waiting).Status);
        Assert.Equal(DecisionStatus.Ng, DetectionRunService.CombineDecision(visualOk, failed).Status);
        Assert.Equal(DecisionStatus.Ok, DetectionRunService.CombineDecision(visualOk, completed).Status);
        Assert.Equal(DecisionStatus.Ok, DetectionRunService.CombineDecision(new DecisionResult(), completed).Status);
    }
}
