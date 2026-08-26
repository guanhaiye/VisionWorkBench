using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class BehaviorRecognitionTests
{
    private static readonly IReadOnlyList<NormalizedPoint> Square =
    [
        new() { X = 0.1, Y = 0.1 },
        new() { X = 0.9, Y = 0.1 },
        new() { X = 0.9, Y = 0.9 },
        new() { X = 0.1, Y = 0.9 },
    ];

    private static BehaviorTrack Person(string id, double x = 0.4, double y = 0.4) => new()
    {
        TrackId = id,
        ClassId = "person",
        Confidence = 0.9,
        Box = new NormalizedRect { X = x, Y = y, Width = 0.1, Height = 0.1 },
    };

    private static BehaviorFrame Frame(double seconds, params BehaviorTrack[] tracks) => new()
    {
        CameraId = "camera-test",
        FrameSequence = (long)(seconds * 10),
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds),
        Tracks = tracks,
    };

    private static IReadOnlyList<Keypoint> Pose(double headY) =>
        Enumerable.Range(0, 17)
            .Select(index => new Keypoint { X = 0.5, Y = index == 0 ? headY : 0.6, Confidence = 0.9 })
            .ToArray();

    [Fact]
    public void Polygon_Boundary_Is_Inside()
    {
        Assert.True(BehaviorEngine.IsPointInPolygon(new NormalizedPoint { X = 0.1, Y = 0.5 }, Square));
        Assert.True(BehaviorEngine.IsPointInPolygon(new NormalizedPoint { X = 0.5, Y = 0.5 }, Square));
        Assert.False(BehaviorEngine.IsPointInPolygon(new NormalizedPoint { X = 0.05, Y = 0.5 }, Square));
    }

    [Fact]
    public void Intrusion_Requires_Confirmation_And_Recovery()
    {
        var engine = new BehaviorEngine(new BehaviorRecognitionConfig
        {
            Zones = [new BehaviorZone { Id = "restricted", Name = "禁入区", Polygon = Square }],
            Intrusion = new BehaviorRuleSettings
            {
                ConfirmingSeconds = 0.5,
                RecoverySeconds = 0.5,
                CooldownSeconds = 0,
            },
            Loitering = new BehaviorRuleSettings { Enabled = false },
            Crowding = new BehaviorRuleSettings { Enabled = false },
        });

        Assert.Empty(engine.Process(Frame(0, Person("p1"))).Events);
        Assert.Empty(engine.Process(Frame(0.4, Person("p1"))).Events);
        var started = Assert.Single(engine.Process(Frame(0.6, Person("p1"))).Events);
        Assert.Equal("intrusion", started.EventType);
        Assert.Equal(VisionEventPhase.Started, started.Phase);

        Assert.Empty(engine.Process(Frame(0.8, Person("p1", 0.0, 0.0))).Events);
        var completed = Assert.Single(engine.Process(Frame(1.2, Person("p1", 0.0, 0.0))).Events);
        Assert.Equal(VisionEventPhase.Completed, completed.Phase);
    }

    [Fact]
    public void Loitering_Emits_Warning_Then_Critical()
    {
        var engine = new BehaviorEngine(new BehaviorRecognitionConfig
        {
            Zones = [new BehaviorZone { Id = "zone-a", Polygon = Square }],
            Intrusion = new BehaviorRuleSettings { Enabled = false },
            Loitering = new BehaviorRuleSettings
            {
                WarningSeconds = 2,
                AlarmSeconds = 4,
                CooldownSeconds = 0,
            },
            Crowding = new BehaviorRuleSettings { Enabled = false },
        });

        Assert.Empty(engine.Process(Frame(0, Person("p1"))).Events);
        var warning = Assert.Single(engine.Process(Frame(2, Person("p1"))).Events);
        Assert.Equal(VisionEventSeverity.Warning, warning.Severity);
        var alarm = Assert.Single(engine.Process(Frame(4, Person("p1"))).Events);
        Assert.Equal(VisionEventSeverity.Critical, alarm.Severity);
    }

    [Fact]
    public void Crowding_Uses_Zone_Count_And_Recovery()
    {
        var engine = new BehaviorEngine(new BehaviorRecognitionConfig
        {
            Zones = [new BehaviorZone { Id = "zone-a", Name = "入口", Polygon = Square }],
            Intrusion = new BehaviorRuleSettings { Enabled = false },
            Loitering = new BehaviorRuleSettings { Enabled = false },
            Crowding = new BehaviorRuleSettings
            {
                WarningCount = 2,
                AlarmCount = 3,
                ConfirmingSeconds = 1,
                RecoverySeconds = 1,
                CooldownSeconds = 0,
            },
        });

        Assert.Empty(engine.Process(Frame(0, Person("p1"), Person("p2"), Person("p3"))).Events);
        var alerts = engine.Process(Frame(1, Person("p1"), Person("p2"), Person("p3"))).Events;
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, e => e.Severity == VisionEventSeverity.Warning);
        Assert.Contains(alerts, e => e.Severity == VisionEventSeverity.Critical);

        Assert.Empty(engine.Process(Frame(1.5)).Events);
        var recovered = Assert.Single(engine.Process(Frame(2.5)).Events);
        Assert.Equal(VisionEventPhase.Completed, recovered.Phase);
    }

    [Fact]
    public void Fall_Requires_Pose_Quality_And_Sustained_Candidate()
    {
        var engine = new BehaviorEngine(new BehaviorRecognitionConfig
        {
            Intrusion = new BehaviorRuleSettings { Enabled = false },
            Loitering = new BehaviorRuleSettings { Enabled = false },
            Crowding = new BehaviorRuleSettings { Enabled = false },
            Fall = new BehaviorRuleSettings { ConfirmingSeconds = 0, RecoverySeconds = 0.5, CooldownSeconds = 0 },
        });
        BehaviorTrack person(double headY) => Person("fall-1") with
        {
            Box = new NormalizedRect { X = 0.2, Y = 0.5, Width = 0.4, Height = 0.2 },
            Keypoints = Pose(headY),
        };

        Assert.Empty(engine.Process(Frame(0, person(0.4))).Events);
        Assert.Empty(engine.Process(Frame(0.5, person(0.65))).Events);
        var started = Assert.Single(engine.Process(Frame(2.1, person(0.65))).Events);
        Assert.Equal("fall", started.EventType);
        Assert.Equal(VisionEventSeverity.Critical, started.Severity);

        var recoveredPerson = Person("fall-1") with
        {
            Box = new NormalizedRect { X = 0.4, Y = 0.3, Width = 0.2, Height = 0.4 },
            Keypoints = Pose(0.4),
        };
        var completed = Assert.Single(engine.Process(Frame(3, recoveredPerson)).Events);
        Assert.Equal(VisionEventPhase.Completed, completed.Phase);
    }
}
