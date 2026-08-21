"""流水线计数内核（文档 §16.3 动态去重 / §16.4 跨线计数）。

检测层复用 sample-counter 的 SimpleCounter（自适应阈值 + 轮廓）；
本模块在其上叠加有状态的多目标跟踪与计数判定：

    检测（归一化框） → IoU 贪心跟踪（Track ID） → 计数事件

- 动态去重（unique）：轨迹确认（连续 minHits 帧）后发一条 Appeared(+1)，
  会话内同一 Track ID 只计一次。
- 跨线计数（line）：轨迹中心对检测线的有符号距离离开滞回带后，还需在对侧
  确认区连续停留 farConfirmFrames 帧才确认换侧并发 CrossedLine(+1,
  forward/reverse)；带内抖动与立即折返均不计（CNT-L-003/004）。

方向约定：n = (B-A) 旋转 90°（图像坐标系下为 (dy, -dx) 归一化）；
forward := 有符号距离 d 由负变正（沿 n 方向穿线）。
竖直检测线取 A=上端 B=下端时，n 指向 +x，左→右运动即 forward。
"""

from __future__ import annotations

import math
import uuid
from datetime import datetime, timezone
from typing import Any

from counter import SimpleCounter

DEFAULT_SETTINGS = {
    "countingMode": "line",      # "unique" | "line"
    "minHits": 3,                # 连续命中确认帧数（CNT-D-006 误检过滤）
    "maxLostFrames": 8,          # 丢失超过该帧数 → 移入去重记忆（CNT-D-003）
    "iouThreshold": 0.3,         # 活跃轨迹匹配阈值
    "lostIouThreshold": 0.05,    # 丢失轨迹低阈值匹配（遮挡恢复）
    "dedupLostFrames": 25,       # 去重窗口：窗口内原位复现不重计（CNT-D-004）
    "trailLength": 30,
    "farConfirmFrames": 2,       # 对侧确认帧数：越过线后须停留该帧数才计数（CNT-L-004）
    "line": {"ax": 0.5, "ay": 0.1, "bx": 0.5, "by": 0.9},
    "hysteresis": 0.02,          # 滞回带半宽（归一化，CNT-L-003）
}

_COUNTING_MODES = ("unique", "line")


def _validate(settings: dict[str, Any]) -> None:
    mode = settings["countingMode"]
    if mode not in _COUNTING_MODES:
        raise ValueError("countingMode 必须为 unique 或 line")
    if int(settings["minHits"]) < 1:
        raise ValueError("minHits 必须 ≥ 1")
    if int(settings["maxLostFrames"]) < 1:
        raise ValueError("maxLostFrames 必须 ≥ 1")
    if not 0 < float(settings["iouThreshold"]) <= 1:
        raise ValueError("iouThreshold 必须在 (0, 1]")
    if not 0 < float(settings["lostIouThreshold"]) <= 1:
        raise ValueError("lostIouThreshold 必须在 (0, 1]")
    if int(settings["dedupLostFrames"]) < 0:
        raise ValueError("dedupLostFrames 必须 ≥ 0")
    if int(settings["trailLength"]) < 1:
        raise ValueError("trailLength 必须 ≥ 1")
    if not 1 <= int(settings["farConfirmFrames"]) <= 20:
        raise ValueError("farConfirmFrames 必须在 [1, 20]")
    if not 0 <= float(settings["hysteresis"]) <= 0.2:
        raise ValueError("hysteresis 必须在 [0, 0.2]")
    if mode == "line":
        line = settings["line"]
        length = math.hypot(line["bx"] - line["ax"], line["by"] - line["ay"])
        if length < 1e-6:
            raise ValueError("检测线两端点不能重合")


def _center(box: dict[str, float]) -> tuple[float, float]:
    return box["x"] + box["width"] / 2.0, box["y"] + box["height"] / 2.0


def _iou(a: dict[str, float], b: dict[str, float]) -> float:
    ax1, ay1 = a["x"], a["y"]
    ax2, ay2 = a["x"] + a["width"], a["y"] + a["height"]
    bx1, by1 = b["x"], b["y"]
    bx2, by2 = b["x"] + b["width"], b["y"] + b["height"]
    ix = max(0.0, min(ax2, bx2) - max(ax1, bx1))
    iy = max(0.0, min(ay2, by2) - max(ay1, by1))
    inter = ix * iy
    if inter <= 0:
        return 0.0
    union = a["width"] * a["height"] + b["width"] * b["height"] - inter
    return inter / union if union > 0 else 0.0


class _Track:
    """单条轨迹（活跃池内，含 tentative 与 lost 未超限）。"""

    __slots__ = (
        "track_id", "box", "class_id", "class_name", "confidence", "trail",
        "hits_streak", "age", "lost", "confirmed", "counted", "last_side",
        "pending_side", "pending_hits", "last_det_index",
    )

    def __init__(self, track_id: int, detection: dict[str, Any]):
        self.track_id = track_id
        self.box = dict(detection["box"])
        self.class_id = detection.get("classId", "object")
        self.class_name = detection.get("className", "物体")
        self.confidence = float(detection.get("confidence", 0.0))
        self.trail: list[dict[str, float]] = [
            {"x": _center(self.box)[0], "y": _center(self.box)[1]}
        ]
        self.hits_streak = 1
        self.age = 1
        self.lost = 0
        self.confirmed = False
        self.counted = False   # unique 模式：已发 Appeared
        self.last_side = 0     # line 模式：最后确认侧（-1/0/+1）
        self.pending_side = 0  # line 模式：候选侧（-1/0/+1）
        self.pending_hits = 0
        self.last_det_index = -1  # 本帧匹配到的检测下标（输出回填 trackId 用）

    def update(self, detection: dict[str, Any], trail_length: int) -> None:
        self.box = dict(detection["box"])
        self.class_id = detection.get("classId", self.class_id)
        self.class_name = detection.get("className", self.class_name)
        self.confidence = float(detection.get("confidence", self.confidence))
        cx, cy = _center(self.box)
        self.trail.append({"x": cx, "y": cy})
        if len(self.trail) > trail_length:
            del self.trail[: len(self.trail) - trail_length]
        self.hits_streak += 1
        self.age += 1
        self.lost = 0


class _Seen:
    """移入去重记忆的历史轨迹（id、最终框、移除帧、计数状态）。"""

    __slots__ = ("track_id", "box", "removed_frame", "counted", "last_side")

    def __init__(self, track: _Track, removed_frame: int):
        self.track_id = track.track_id
        self.box = dict(track.box)
        self.removed_frame = removed_frame
        self.counted = track.counted
        self.last_side = track.last_side


class IoUTracker:
    """贪心 IoU 多目标跟踪（无 scipy 依赖）。"""

    def __init__(self, settings: dict[str, Any]):
        self.settings = settings
        self.tracks: list[_Track] = []
        self.seen: list[_Seen] = []
        self._next_id = 1
        self._frame = 0

    def reset(self) -> None:
        self.tracks = []
        self.seen = []
        self._next_id = 1
        self._frame = 0

    def update(self, detections: list[dict[str, Any]]) -> list[_Track]:
        """推进一帧；返回当前活跃轨迹（含 tentative）。"""
        self._frame += 1
        min_hits = int(self.settings["minHits"])
        max_lost = int(self.settings["maxLostFrames"])
        iou_thr = float(self.settings["iouThreshold"])
        lost_iou_thr = float(self.settings["lostIouThreshold"])
        trail_len = int(self.settings["trailLength"])

        # 1. 贪心 IoU 配对：全局降序取最优（丢失轨用低阈值参与 → 遮挡恢复）
        pairs: list[tuple[float, int, int]] = []
        for ti, track in enumerate(self.tracks):
            threshold = lost_iou_thr if track.lost > 0 else iou_thr
            for di, det in enumerate(detections):
                score = _iou(track.box, det["box"])
                if score >= threshold:
                    pairs.append((score, ti, di))
        pairs.sort(key=lambda p: p[0], reverse=True)
        matched_tracks: set[int] = set()
        matched_dets: set[int] = set()
        for _, ti, di in pairs:
            if ti in matched_tracks or di in matched_dets:
                continue
            matched_tracks.add(ti)
            matched_dets.add(di)
            track = self.tracks[ti]
            track.update(detections[di], trail_len)
            track.last_det_index = di

        # 2. 未匹配轨迹：丢失 +1，超限移入去重记忆
        retired: list[_Track] = []
        for ti, track in enumerate(self.tracks):
            if ti in matched_tracks:
                continue
            track.lost += 1
            track.age += 1
            track.hits_streak = 0
            if track.lost > max_lost:
                retired.append(track)
        if retired:
            # 仅淘汰超限轨迹；已匹配轨迹 lost 已清零，天然保留
            self.tracks = [t for t in self.tracks if t.lost <= max_lost]
            for track in retired:
                self.seen.append(_Seen(track, self._frame))

        # 3. 未匹配检测：新建 tentative 轨迹（不发事件，CNT-D-006）
        for di, det in enumerate(detections):
            if di not in matched_dets:
                track = _Track(self._next_id, det)
                track.last_det_index = di
                self.tracks.append(track)
                self._next_id += 1

        # 4. 确认与去重继承：hits_streak ≥ minHits 的轨迹转 confirmed；
        #    去重窗口内与历史轨迹原位（IoU 达阈值）重合 → 继承计数状态不重计
        newly_confirmed: list[_Track] = []
        for track in self.tracks:
            if track.confirmed or track.hits_streak < min_hits:
                continue
            track.confirmed = True
            newly_confirmed.append(track)
            window = int(self.settings["dedupLostFrames"])
            for si in range(len(self.seen) - 1, -1, -1):
                entry = self.seen[si]
                if self._frame - entry.removed_frame > window:
                    break  # seen 按移除时间升序，更早的必然超窗
                if _iou(track.box, entry.box) >= iou_thr:
                    track.counted = entry.counted
                    track.last_side = entry.last_side
                    del self.seen[si]
                    break
        return newly_confirmed


class FlowCounter:
    """有状态流水线计数器：检测 → 跟踪 → 计数事件。"""

    def __init__(self, settings: dict[str, Any] | None = None):
        merged: dict[str, Any] = dict(DEFAULT_SETTINGS)
        merged.update(settings or {})
        line = dict(merged.get("line") or {})
        for key in ("ax", "ay", "bx", "by"):
            line.setdefault(DEFAULT_SETTINGS["line"][key])
        merged["line"] = line
        _validate(merged)
        self.settings = merged
        self._detector = SimpleCounter(merged)  # 额外键被 SimpleCounter 忽略
        self._tracker = IoUTracker(merged)
        self._mode = merged["countingMode"]
        # 检测线几何（缓存法向量）
        line = merged["line"]
        self._a = (float(line["ax"]), float(line["ay"]))
        dx = float(line["bx"]) - self._a[0]
        dy = float(line["by"]) - self._a[1]
        norm = math.hypot(dx, dy)
        # n = (B-A) 旋转 90°（图像坐标系 (dy, -dx)）：竖直线 A上B下 → n 指向 +x
        self._normal = (dy / norm, -dx / norm)
        self._hysteresis = float(merged["hysteresis"])
        self._far_confirm = int(merged["farConfirmFrames"])

    def reset_session(self) -> None:
        """会话重建/计数清零：清除轨迹与去重记忆（模型与配置保留）。"""
        self._tracker.reset()

    def process(self, image_path: str, frame_sequence: int = 0) -> dict[str, Any]:
        detection_result = self._detector.process(image_path)
        result = self.update(detection_result["detections"], frame_sequence)
        result["performance"] = detection_result["performance"]
        return result

    def update(
        self, detections: list[dict[str, Any]], frame_sequence: int = 0
    ) -> dict[str, Any]:
        """以一帧检测结果推进跟踪与计数（process 的检测后半段，供测试直驱）。"""
        started = datetime.now(timezone.utc)
        detections = [dict(d) for d in detections]
        newly_confirmed = self._tracker.update(detections)

        # 检测框回填 trackId（仅 confirmed 轨迹；tentative 对宿主无意义）
        for track in self._tracker.tracks:
            if track.confirmed and track.lost == 0 and track.last_det_index >= 0:
                det = detections[track.last_det_index]
                if det is not None:
                    det["trackId"] = str(track.track_id)

        events: list[dict[str, Any]] = []
        if self._mode == "unique":
            for track in newly_confirmed:
                if not track.counted:
                    track.counted = True
                    events.append(self._event(
                        track, frame_sequence, "appeared", delta=1, occurred_at=started))
        else:
            for track in self._tracker.tracks:
                if not track.confirmed:
                    continue
                event = self._check_line_crossing(track, frame_sequence, started)
                if event is not None:
                    events.append(event)

        tracks_out = [
            {
                "trackId": str(t.track_id),
                "classId": t.class_id,
                "box": dict(t.box),
                "trail": [dict(p) for p in t.trail],
                "age": t.age,
                "timeSinceUpdate": t.lost,
            }
            for t in self._tracker.tracks if t.confirmed
        ]
        visible = sum(1 for t in self._tracker.tracks if t.confirmed and t.lost == 0)

        return {
            "detections": detections,
            "tracks": tracks_out,
            "countingEvents": events,
            "metrics": [
                {"name": "count", "value": visible, "unit": "个"},
            ],
        }

    def _check_line_crossing(
        self, track: _Track, frame_sequence: int, occurred_at: datetime
    ) -> dict[str, Any] | None:
        """滞回 + 对侧确认状态机（CNT-L-003/004）。

        带内不判定；离开滞回带进入对侧确认区后须连续 farConfirmFrames 帧
        才确认换侧发 CrossedLine——中途返回原侧（pending 被清零）不计。
        """
        cx, cy = _center(track.box)
        d = (cx - self._a[0]) * self._normal[0] + (cy - self._a[1]) * self._normal[1]
        if abs(d) <= self._hysteresis + 1e-9:  # epsilon：吸收带边界浮点误差
            track.pending_side = 0  # 带内抖动：取消候选，不更新确认侧
            track.pending_hits = 0
            return None
        side = 1 if d > 0 else -1
        if track.last_side == 0:
            track.last_side = side  # 首次观测只确立初始侧，不发事件
            return None
        if side == track.last_side:
            track.pending_side = 0  # 返回原侧：立即折返不计（CNT-L-004）
            track.pending_hits = 0
            return None
        if track.pending_side != side:
            track.pending_side = side
            track.pending_hits = 1
        else:
            track.pending_hits += 1
        if track.pending_hits < self._far_confirm:
            return None
        track.last_side = side
        track.pending_side = 0
        track.pending_hits = 0
        direction = "forward" if side > 0 else "reverse"
        return self._event(
            track, frame_sequence, "crossedLine", delta=1,
            direction=direction, occurred_at=occurred_at)

    @staticmethod
    def _event(
        track: _Track, frame_sequence: int, type_: str, delta: int,
        direction: str | None = None, occurred_at: datetime | None = None,
    ) -> dict[str, Any]:
        event: dict[str, Any] = {
            "eventId": "evt-" + uuid.uuid4().hex[:12],
            "counterId": "default",
            "trackId": str(track.track_id),
            "classId": track.class_id,
            "type": type_,
            "delta": delta,
            "confidence": round(track.confidence, 3),
            "occurredAt": (occurred_at or datetime.now(timezone.utc)).isoformat(),
            "frameSequence": frame_sequence,
        }
        if direction is not None:
            event["direction"] = direction
        return event
