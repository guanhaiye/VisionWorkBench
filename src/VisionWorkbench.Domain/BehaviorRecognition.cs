using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Domain;

/// <summary>首期行为识别类型。复杂动作（跌倒、打架）在此基础上接入时序模型。</summary>
public enum BehaviorKind
{
    Intrusion,
    Loitering,
    Crowding,
    Fall,
}

/// <summary>行为识别区域，使用 0~1 归一化多边形坐标。</summary>
public sealed record BehaviorZone
{
    public required string Id { get; init; }
    public string Name { get; init; } = "区域";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<NormalizedPoint> Polygon { get; init; } = [];
}

/// <summary>单类行为规则参数。</summary>
public sealed record BehaviorRuleSettings
{
    public bool Enabled { get; init; } = true;
    public double MinimumConfidence { get; init; } = 0.5;
    public double ConfirmingSeconds { get; init; } = 0.5;
    public double RecoverySeconds { get; init; } = 1.0;
    public double CooldownSeconds { get; init; } = 10.0;
    public double WarningSeconds { get; init; } = 30.0;
    public double AlarmSeconds { get; init; } = 60.0;
    public int WarningCount { get; init; } = 5;
    public int AlarmCount { get; init; } = 8;
    public double MaximumMissingSeconds { get; init; } = 2.0;
}

/// <summary>任务级行为识别配置，随 Recipe 一起保存。</summary>
public sealed record BehaviorRecognitionConfig
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<BehaviorZone> Zones { get; init; } = [];
    public BehaviorRuleSettings Intrusion { get; init; } = new();
    public BehaviorRuleSettings Loitering { get; init; } = new();
    public BehaviorRuleSettings Crowding { get; init; } = new();
    public BehaviorRuleSettings Fall { get; init; } = new();
}

/// <summary>行为引擎每帧接收的已跟踪人员。</summary>
public sealed record BehaviorTrack
{
    public required string TrackId { get; init; }
    public string ClassId { get; init; } = "person";
    public double Confidence { get; init; } = 1.0;
    public required NormalizedRect Box { get; init; }
    public IReadOnlyList<Keypoint> Keypoints { get; init; } = [];
    public double PoseQuality { get; init; }
    public double? FallProbability { get; init; }
    public string? TemporalModel { get; init; }
    public string? BehaviorClass { get; init; }
    public double? BehaviorProbability { get; init; }

    public NormalizedPoint FootPoint => new()
    {
        X = Box.CenterX,
        Y = Math.Clamp(Box.Y + Box.Height, 0, 1),
    };
}

/// <summary>行为引擎的帧输入。持续时间全部使用时间戳计算，不依赖固定 FPS。</summary>
public sealed record BehaviorFrame
{
    public required string CameraId { get; init; }
    public long FrameSequence { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<BehaviorTrack> Tracks { get; init; } = [];

    public static BehaviorFrame FromAlgorithmOutput(
        string cameraId, AlgorithmOutput output, long frameSequence, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(output);
        var confidenceByTrack = output.Detections
            .Where(d => !string.IsNullOrWhiteSpace(d.TrackId))
            .GroupBy(d => d.TrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(d => d.Confidence), StringComparer.Ordinal);
        var classByTrack = output.Detections
            .Where(d => !string.IsNullOrWhiteSpace(d.TrackId))
            .GroupBy(d => d.TrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ClassId, StringComparer.Ordinal);
        var keypointsByTrack = output.Keypoints
            .Where(k => !string.IsNullOrWhiteSpace(k.TrackId))
            .GroupBy(k => k.TrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Keypoint>)g.First().Points, StringComparer.Ordinal);
        var poseByTrack = output.Keypoints
            .Where(k => !string.IsNullOrWhiteSpace(k.TrackId))
            .GroupBy(k => k.TrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var tracks = output.Tracks.Select(track => new BehaviorTrack
        {
            TrackId = track.TrackId,
            ClassId = string.IsNullOrWhiteSpace(track.ClassId)
                ? classByTrack.GetValueOrDefault(track.TrackId, "person")
                : track.ClassId,
            Confidence = confidenceByTrack.GetValueOrDefault(track.TrackId, 1.0),
            Box = track.Box,
            Keypoints = keypointsByTrack.GetValueOrDefault(track.TrackId, []),
            PoseQuality = poseByTrack.GetValueOrDefault(track.TrackId)?.PoseQuality ?? 0,
            FallProbability = poseByTrack.GetValueOrDefault(track.TrackId)?.FallProbability,
            TemporalModel = poseByTrack.GetValueOrDefault(track.TrackId)?.TemporalModel,
            BehaviorClass = poseByTrack.GetValueOrDefault(track.TrackId)?.BehaviorClass,
            BehaviorProbability = poseByTrack.GetValueOrDefault(track.TrackId)?.BehaviorProbability,
        }).ToList();

        // 兼容只在 DetectionResult 上携带 TrackId 的算法插件。
        var knownIds = tracks.Select(t => t.TrackId).ToHashSet(StringComparer.Ordinal);
        tracks.AddRange(output.Detections
            .Where(d => !string.IsNullOrWhiteSpace(d.TrackId) && !knownIds.Contains(d.TrackId!))
            .GroupBy(d => d.TrackId!, StringComparer.Ordinal)
            .Select(g =>
            {
                var detection = g.First();
                return new BehaviorTrack
                {
                    TrackId = g.Key,
                    ClassId = detection.ClassId,
                    Confidence = g.Max(d => d.Confidence),
                    Box = detection.Box,
                    Keypoints = keypointsByTrack.GetValueOrDefault(g.Key, []),
                    PoseQuality = poseByTrack.GetValueOrDefault(g.Key)?.PoseQuality ?? 0,
                    FallProbability = poseByTrack.GetValueOrDefault(g.Key)?.FallProbability,
                    TemporalModel = poseByTrack.GetValueOrDefault(g.Key)?.TemporalModel,
                    BehaviorClass = poseByTrack.GetValueOrDefault(g.Key)?.BehaviorClass,
                    BehaviorProbability = poseByTrack.GetValueOrDefault(g.Key)?.BehaviorProbability,
                };
            }));

        return new BehaviorFrame
        {
            CameraId = cameraId,
            FrameSequence = frameSequence,
            Timestamp = timestamp,
            Tracks = tracks,
        };
    }
}

public sealed record BehaviorEvaluation
{
    public IReadOnlyList<VisionEvent> Events { get; init; } = [];
}

/// <summary>
/// 首期行为规则引擎：区域闯入、滞留和聚集。
/// 每个摄像头/任务应创建独立实例，内部状态不会跨任务共享。
/// </summary>
public sealed class BehaviorEngine
{
    private readonly BehaviorRecognitionConfig _config;
    private readonly Dictionary<TrackZoneKey, TrackZoneState> _trackStates = [];
    private readonly Dictionary<string, CrowdState> _crowdStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FallState> _fallStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _customBehaviorStates = new(StringComparer.Ordinal);

    private readonly record struct TrackZoneKey(string TrackId, string ZoneId, BehaviorKind Kind);

    private sealed class TrackZoneState
    {
        public DateTimeOffset? EnteredAt { get; set; }
        public DateTimeOffset? LastInsideAt { get; set; }
        public DateTimeOffset LastObservedAt { get; set; }
        public DateTimeOffset? LastAlarmAt { get; set; }
        public bool Active { get; set; }
        public bool WarningRaised { get; set; }
        public bool AlarmRaised { get; set; }
    }

    private sealed class CrowdState
    {
        public DateTimeOffset? AboveWarningAt { get; set; }
        public DateTimeOffset? LastAboveAt { get; set; }
        public DateTimeOffset? LastAlarmAt { get; set; }
        public bool WarningRaised { get; set; }
        public bool AlarmRaised { get; set; }
    }

    private sealed class FallState
    {
        public Queue<FallSample> Samples { get; } = new();
        public DateTimeOffset? CandidateSince { get; set; }
        public DateTimeOffset? LastCandidateAt { get; set; }
        public DateTimeOffset? LastAlarmAt { get; set; }
        public DateTimeOffset LastObservedAt { get; set; }
        public bool Active { get; set; }
        public double LastScore { get; set; }
    }

    private sealed record FallSample(DateTimeOffset Timestamp, NormalizedRect Box, IReadOnlyList<Keypoint> Keypoints);

    public BehaviorEngine(BehaviorRecognitionConfig? config = null)
    {
        _config = config ?? new BehaviorRecognitionConfig();
    }

    public BehaviorEvaluation Process(BehaviorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_config.Enabled || frame.Timestamp == default)
        {
            return new BehaviorEvaluation();
        }

        var events = new List<VisionEvent>();
        var activeTracks = frame.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.TrackId)
                && t.ClassId.Equals("person", StringComparison.OrdinalIgnoreCase)
                && (t.Confidence >= MinimumEnabledConfidence()
                    || !string.IsNullOrWhiteSpace(t.BehaviorClass)))
            .GroupBy(t => t.TrackId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToArray();

        ProcessCustomBehaviors(frame, activeTracks, events);

        foreach (var zone in _config.Zones.Where(z => z.Enabled && z.Polygon.Count >= 3))
        {
            foreach (var track in activeTracks)
            {
                var inside = IsPointInPolygon(track.FootPoint, zone.Polygon);
                ProcessIntrusion(frame, zone, track, inside, events);
                ProcessLoitering(frame, zone, track, inside, events);
            }

            ProcessCrowding(frame, zone, activeTracks, events);
        }

        ProcessFalls(frame, activeTracks, events);

        RecoverMissingTracks(frame, activeTracks, events);
        return new BehaviorEvaluation { Events = events };
    }

    public void Reset()
    {
        _trackStates.Clear();
        _crowdStates.Clear();
        _fallStates.Clear();
        _customBehaviorStates.Clear();
    }

    private void ProcessCustomBehaviors(
        BehaviorFrame frame, IReadOnlyList<BehaviorTrack> tracks, List<VisionEvent> events)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.BehaviorClass)
                || track.BehaviorProbability is not { } probability
                || probability < 0.5)
            {
                continue;
            }

            observed.Add(track.TrackId);
            var label = track.BehaviorClass.Trim();
            if (_customBehaviorStates.TryGetValue(track.TrackId, out var previous)
                && string.Equals(previous, label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _customBehaviorStates[track.TrackId] = label;
            events.Add(CreateEvent(
                $"behavior:{label}", VisionEventPhase.Started, VisionEventSeverity.Info,
                track.TrackId, null, frame.Timestamp, frame.Timestamp, probability,
                $"人员 {track.TrackId} 行为：{label}（置信度 {probability:0.00}）"));
        }

        foreach (var trackId in _customBehaviorStates.Keys.Except(observed, StringComparer.Ordinal).ToArray())
        {
            var previous = _customBehaviorStates[trackId];
            events.Add(CreateEvent(
                $"behavior:{previous}", VisionEventPhase.Completed, VisionEventSeverity.Info,
                trackId, null, frame.Timestamp, frame.Timestamp, 0,
                $"人员 {trackId} 行为识别结束：{previous}"));
            _customBehaviorStates.Remove(trackId);
        }
    }

    private void ProcessIntrusion(
        BehaviorFrame frame, BehaviorZone zone, BehaviorTrack track, bool inside, List<VisionEvent> events)
    {
        var settings = _config.Intrusion;
        if (!settings.Enabled || track.Confidence < settings.MinimumConfidence)
        {
            return;
        }

        var state = GetTrackState(track.TrackId, zone.Id, BehaviorKind.Intrusion);
        state.LastObservedAt = frame.Timestamp;
        if (inside)
        {
            state.LastInsideAt = frame.Timestamp;
            state.EnteredAt ??= frame.Timestamp;
            if (!state.Active
                && Elapsed(state.EnteredAt, frame.Timestamp) >= Positive(settings.ConfirmingSeconds)
                && CanTrigger(state.LastAlarmAt, frame.Timestamp, settings.CooldownSeconds))
            {
                state.Active = true;
                state.LastAlarmAt = frame.Timestamp;
                events.Add(CreateEvent(
                    "intrusion", VisionEventPhase.Started, VisionEventSeverity.Warning,
                    track.TrackId, zone.Id, state.EnteredAt, frame.Timestamp,
                    track.Confidence, $"人员 {track.TrackId} 进入区域 {zone.Name}"));
            }
        }
        else
        {
            RecoverTrackState(frame, zone, track, state, BehaviorKind.Intrusion, events,
                "intrusion", "人员离开区域");
        }
    }

    private void ProcessLoitering(
        BehaviorFrame frame, BehaviorZone zone, BehaviorTrack track, bool inside, List<VisionEvent> events)
    {
        var settings = _config.Loitering;
        if (!settings.Enabled || track.Confidence < settings.MinimumConfidence)
        {
            return;
        }

        var state = GetTrackState(track.TrackId, zone.Id, BehaviorKind.Loitering);
        state.LastObservedAt = frame.Timestamp;
        if (inside)
        {
            state.LastInsideAt = frame.Timestamp;
            state.EnteredAt ??= frame.Timestamp;
            var elapsed = Elapsed(state.EnteredAt, frame.Timestamp);
            if (!state.WarningRaised
                && elapsed >= Positive(settings.WarningSeconds)
                && CanTrigger(state.LastAlarmAt, frame.Timestamp, settings.CooldownSeconds))
            {
                state.WarningRaised = true;
                state.LastAlarmAt = frame.Timestamp;
                events.Add(CreateEvent(
                    "loitering", VisionEventPhase.Started, VisionEventSeverity.Warning,
                    track.TrackId, zone.Id, state.EnteredAt, frame.Timestamp,
                    track.Confidence, $"人员 {track.TrackId} 在区域 {zone.Name} 滞留超过 {settings.WarningSeconds:0.#} 秒"));
            }

            if (!state.AlarmRaised && elapsed >= Positive(settings.AlarmSeconds))
            {
                state.AlarmRaised = true;
                events.Add(CreateEvent(
                    "loitering", VisionEventPhase.Updated, VisionEventSeverity.Critical,
                    track.TrackId, zone.Id, state.EnteredAt, frame.Timestamp,
                    track.Confidence, $"人员 {track.TrackId} 在区域 {zone.Name} 滞留超过 {settings.AlarmSeconds:0.#} 秒"));
            }
        }
        else
        {
            RecoverTrackState(frame, zone, track, state, BehaviorKind.Loitering, events,
                "loitering", "人员结束区域滞留");
        }
    }

    private void ProcessFalls(BehaviorFrame frame, IReadOnlyList<BehaviorTrack> tracks, List<VisionEvent> events)
    {
        var settings = _config.Fall;
        if (!settings.Enabled)
        {
            return;
        }

        var observedIds = tracks.Select(track => track.TrackId).ToHashSet(StringComparer.Ordinal);
        foreach (var track in tracks.Where(track => track.Confidence >= settings.MinimumConfidence))
        {
            var state = _fallStates.TryGetValue(track.TrackId, out var existing)
                ? existing
                : (_fallStates[track.TrackId] = new FallState());
            state.LastObservedAt = frame.Timestamp;
            state.Samples.Enqueue(new FallSample(frame.Timestamp, track.Box, track.Keypoints));
            while (state.Samples.Count > 30 || (state.Samples.TryPeek(out var oldest)
                && (frame.Timestamp - oldest.Timestamp).TotalSeconds > 3))
            {
                state.Samples.Dequeue();
            }

            var geometricScore = FallCandidateScore(state.Samples, out var geometricPoseQuality);
            var poseQuality = track.PoseQuality > 0 ? track.PoseQuality : geometricPoseQuality;
            var score = track.FallProbability is { } modelScore
                ? Math.Clamp(0.55 * modelScore + 0.45 * geometricScore, 0, 1)
                : geometricScore;
            state.LastScore = score;
            if (poseQuality >= 0.5 && score >= 0.55)
            {
                state.CandidateSince ??= frame.Timestamp;
                state.LastCandidateAt = frame.Timestamp;
                var requiredSeconds = Math.Max(1.5, settings.ConfirmingSeconds);
                if (!state.Active
                    && score >= 0.75
                    && Elapsed(state.CandidateSince, frame.Timestamp) >= requiredSeconds
                    && CanTrigger(state.LastAlarmAt, frame.Timestamp, settings.CooldownSeconds))
                {
                    state.Active = true;
                    state.LastAlarmAt = frame.Timestamp;
                    events.Add(CreateEvent(
                        "fall", VisionEventPhase.Started, VisionEventSeverity.Critical,
                        track.TrackId, null, state.CandidateSince, frame.Timestamp,
                        score, $"检测到人员 {track.TrackId} 疑似跌倒，姿态评分 {score:0.##}"));
                }
            }
            else if (state.CandidateSince is { } candidateSince
                && state.LastCandidateAt is { } lastCandidate
                && (frame.Timestamp - lastCandidate).TotalSeconds >= Positive(settings.RecoverySeconds))
            {
                CompleteFall(frame.Timestamp, track.TrackId, state, events);
            }
        }

        foreach (var pair in _fallStates.ToArray())
        {
            if (observedIds.Contains(pair.Key)
                || (frame.Timestamp - pair.Value.LastObservedAt).TotalSeconds < Positive(settings.MaximumMissingSeconds))
            {
                continue;
            }
            if (pair.Value.Active)
            {
                CompleteFall(frame.Timestamp, pair.Key, pair.Value, events);
            }
            _fallStates.Remove(pair.Key);
        }
    }

    private static void CompleteFall(
        DateTimeOffset timestamp, string trackId, FallState state, List<VisionEvent> events)
    {
        if (state.Active)
        {
            events.Add(CreateEvent(
                "fall", VisionEventPhase.Completed, VisionEventSeverity.Critical,
                trackId, null, state.CandidateSince, timestamp, state.LastScore,
                $"人员 {trackId} 跌倒状态恢复或目标离开"));
        }
        state.Active = false;
        state.CandidateSince = null;
        state.LastCandidateAt = null;
    }

    private static double FallCandidateScore(
        IEnumerable<FallSample> samples, out double poseQuality)
    {
        var current = samples.LastOrDefault();
        if (current is null || current.Keypoints.Count < 13)
        {
            poseQuality = 0;
            return 0;
        }

        var visible = current.Keypoints.Count(point => point.Confidence > 0.3);
        poseQuality = Math.Clamp(visible / 17.0, 0, 1);
        var ratio = current.Box.Width / Math.Max(current.Box.Height, 1e-6);
        var horizontalScore = Math.Clamp((ratio - 0.8) / 1.2, 0, 1);
        var descentScore = 0.0;
        var previous = samples.FirstOrDefault(sample => TryKeypoint(sample.Keypoints, 0, out _));
        if (previous is not null
            && TryKeypoint(current.Keypoints, 0, out var head)
            && TryKeypoint(previous.Keypoints, 0, out var previousHead))
        {
            descentScore = Math.Clamp((head.Y - previousHead.Y) / 0.2, 0, 1);
        }
        var lowPositionScore = TryKeypoint(current.Keypoints, 0, out var currentHead)
            ? Math.Clamp((currentHead.Y - 0.45) / 0.4, 0, 1)
            : 0;
        var bodyHorizontalScore = Math.Clamp((ratio - 1.0) / 1.0, 0, 1);
        return Math.Clamp(
            0.30 * bodyHorizontalScore
            + 0.25 * descentScore
            + 0.20 * horizontalScore
            + 0.15 * lowPositionScore
            + 0.10 * horizontalScore, 0, 1);
    }

    private static bool TryKeypoint(IReadOnlyList<Keypoint> points, int index, out Keypoint point)
    {
        if (index >= 0 && index < points.Count && points[index].Confidence > 0.3)
        {
            point = points[index];
            return true;
        }
        point = new Keypoint();
        return false;
    }

    private void ProcessCrowding(
        BehaviorFrame frame, BehaviorZone zone, IReadOnlyList<BehaviorTrack> tracks, List<VisionEvent> events)
    {
        var settings = _config.Crowding;
        if (!settings.Enabled)
        {
            return;
        }

        var count = tracks.Count(t => t.Confidence >= settings.MinimumConfidence
            && IsPointInPolygon(t.FootPoint, zone.Polygon));
        var state = _crowdStates.TryGetValue(zone.Id, out var existing)
            ? existing
            : (_crowdStates[zone.Id] = new CrowdState());
        var timestamp = frame.Timestamp;

        if (count >= Math.Max(1, settings.WarningCount))
        {
            state.AboveWarningAt ??= timestamp;
            state.LastAboveAt = timestamp;
            var elapsed = Elapsed(state.AboveWarningAt, timestamp);
            if (!state.WarningRaised
                && elapsed >= Positive(settings.ConfirmingSeconds)
                && CanTrigger(state.LastAlarmAt, timestamp, settings.CooldownSeconds))
            {
                state.WarningRaised = true;
                state.LastAlarmAt = timestamp;
                events.Add(CreateEvent(
                    "crowding", VisionEventPhase.Started, VisionEventSeverity.Warning,
                    null, zone.Id, state.AboveWarningAt, timestamp, 1.0,
                    $"区域 {zone.Name} 人员数量达到 {count}，超过预警阈值 {settings.WarningCount}"));
            }

            if (!state.AlarmRaised && count >= Math.Max(settings.WarningCount, settings.AlarmCount)
                && elapsed >= Positive(settings.ConfirmingSeconds))
            {
                state.AlarmRaised = true;
                events.Add(CreateEvent(
                    "crowding", VisionEventPhase.Updated, VisionEventSeverity.Critical,
                    null, zone.Id, state.AboveWarningAt, timestamp, 1.0,
                    $"区域 {zone.Name} 人员数量达到 {count}，超过报警阈值 {settings.AlarmCount}"));
            }
        }
        else if (state.AboveWarningAt is not null
            && state.LastAboveAt is { } lastAbove
            && (timestamp - lastAbove).TotalSeconds >= Positive(settings.RecoverySeconds))
        {
            if (state.WarningRaised || state.AlarmRaised)
            {
                events.Add(CreateEvent(
                    "crowding", VisionEventPhase.Completed,
                    state.AlarmRaised ? VisionEventSeverity.Critical : VisionEventSeverity.Warning,
                    null, zone.Id, state.AboveWarningAt, timestamp, 1.0,
                    $"区域 {zone.Name} 人员数量已恢复到 {count}"));
            }
            state.AboveWarningAt = null;
            state.LastAboveAt = null;
            state.WarningRaised = false;
            state.AlarmRaised = false;
        }
    }

    private void RecoverTrackState(
        BehaviorFrame frame, BehaviorZone zone, BehaviorTrack track, TrackZoneState state,
        BehaviorKind kind, List<VisionEvent> events, string eventType, string message)
    {
        if (state.EnteredAt is null)
        {
            return;
        }

        var settings = Settings(kind);
        var lastInside = state.LastInsideAt ?? state.EnteredAt.Value;
        if ((frame.Timestamp - lastInside).TotalSeconds < Positive(settings.RecoverySeconds))
        {
            return;
        }

        if (state.Active || state.WarningRaised || state.AlarmRaised)
        {
            events.Add(CreateEvent(
                eventType, VisionEventPhase.Completed,
                state.AlarmRaised ? VisionEventSeverity.Critical : VisionEventSeverity.Warning,
                track.TrackId, zone.Id, state.EnteredAt, frame.Timestamp,
                track.Confidence, $"{message}：{track.TrackId}，区域 {zone.Name}"));
        }
        state.EnteredAt = null;
        state.LastInsideAt = null;
        state.Active = false;
        state.WarningRaised = false;
        state.AlarmRaised = false;
    }

    private void RecoverMissingTracks(
        BehaviorFrame frame, IReadOnlyList<BehaviorTrack> activeTracks, List<VisionEvent> events)
    {
        var activeIds = activeTracks.Select(t => t.TrackId).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in _trackStates.ToArray())
        {
            var state = pair.Value;
            if (activeIds.Contains(pair.Key.TrackId)
                || (frame.Timestamp - state.LastObservedAt).TotalSeconds < Positive(Settings(pair.Key.Kind).MaximumMissingSeconds))
            {
                continue;
            }

            if (state.Active || state.WarningRaised || state.AlarmRaised)
            {
                events.Add(CreateEvent(
                    pair.Key.Kind switch
                    {
                        BehaviorKind.Intrusion => "intrusion",
                        BehaviorKind.Loitering => "loitering",
                        _ => pair.Key.Kind.ToString().ToLowerInvariant(),
                    },
                    VisionEventPhase.Completed,
                    state.AlarmRaised ? VisionEventSeverity.Critical : VisionEventSeverity.Warning,
                    pair.Key.TrackId, pair.Key.ZoneId, state.EnteredAt, frame.Timestamp, 0,
                    $"跟踪目标 {pair.Key.TrackId} 丢失，结束行为状态"));
            }
            _trackStates.Remove(pair.Key);
        }
    }

    private TrackZoneState GetTrackState(string trackId, string zoneId, BehaviorKind kind)
    {
        var key = new TrackZoneKey(trackId, zoneId, kind);
        if (!_trackStates.TryGetValue(key, out var state))
        {
            state = new TrackZoneState();
            _trackStates[key] = state;
        }
        return state;
    }

    private BehaviorRuleSettings Settings(BehaviorKind kind) => kind switch
    {
        BehaviorKind.Intrusion => _config.Intrusion,
        BehaviorKind.Loitering => _config.Loitering,
        BehaviorKind.Crowding => _config.Crowding,
        BehaviorKind.Fall => _config.Fall,
        _ => _config.Intrusion,
    };

    private double MinimumEnabledConfidence()
    {
        var enabled = new[] { _config.Intrusion, _config.Loitering, _config.Crowding, _config.Fall }
            .Where(settings => settings.Enabled)
            .Select(settings => settings.MinimumConfidence)
            .ToArray();
        return enabled.Length == 0 ? 0 : enabled.Min();
    }

    private static double Positive(double value) => Math.Max(0, value);

    private static double Elapsed(DateTimeOffset? start, DateTimeOffset now)
        => start is { } value ? Math.Max(0, (now - value).TotalSeconds) : 0;

    private static bool CanTrigger(DateTimeOffset? lastAlarm, DateTimeOffset now, double cooldownSeconds)
        => lastAlarm is null || (now - lastAlarm.Value).TotalSeconds >= Positive(cooldownSeconds);

    private static VisionEvent CreateEvent(
        string eventType, VisionEventPhase phase, VisionEventSeverity severity,
        string? subjectId, string? regionId, DateTimeOffset? startedAt,
        DateTimeOffset now, double confidence, string message)
        => new()
        {
            EventId = $"beh-{Guid.NewGuid():N}",
            EventType = eventType,
            Phase = phase,
            Severity = severity,
            SubjectId = subjectId,
            RegionId = regionId,
            StartedAt = startedAt,
            EndedAt = phase == VisionEventPhase.Completed ? now : null,
            Confidence = Math.Clamp(confidence, 0, 1),
            Message = message,
        };

    /// <summary>射线法判断点是否在多边形内；边界点按区域内处理。</summary>
    public static bool IsPointInPolygon(NormalizedPoint point, IReadOnlyList<NormalizedPoint> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (PointOnSegment(point, a, b))
            {
                return true;
            }
            var crosses = (a.Y > point.Y) != (b.Y > point.Y);
            if (crosses)
            {
                var xAtY = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
                if (point.X < xAtY)
                {
                    inside = !inside;
                }
            }
        }
        return inside;
    }

    private static bool PointOnSegment(NormalizedPoint p, NormalizedPoint a, NormalizedPoint b)
    {
        const double epsilon = 1e-9;
        var cross = (p.Y - a.Y) * (b.X - a.X) - (p.X - a.X) * (b.Y - a.Y);
        if (Math.Abs(cross) > epsilon)
        {
            return false;
        }
        return p.X >= Math.Min(a.X, b.X) - epsilon
            && p.X <= Math.Max(a.X, b.X) + epsilon
            && p.Y >= Math.Min(a.Y, b.Y) - epsilon
            && p.Y <= Math.Max(a.Y, b.Y) + epsilon;
    }
}
