"""sample-flow-counter 单元测试：跟踪与计数状态机（CNT-D / CNT-L 对应用例）。

状态机级用例直接以合成检测框驱动 FlowCounter.update（精确控制帧序列）；
集成级用例走真实检测（SimpleCounter）+ 图片文件。
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np
import pytest

PLUGIN_DIR = Path(__file__).resolve().parent.parent
for _path in (PLUGIN_DIR, PLUGIN_DIR.parent / "sample-counter"):
    if str(_path) not in sys.path:
        sys.path.insert(0, str(_path))

from flow_counter import FlowCounter, DEFAULT_SETTINGS  # noqa: E402

# 检测线：竖直 x=0.5（A 上 B 下）→ forward = 左→右（见 flow_counter 方向约定）
LINE = {"ax": 0.5, "ay": 0.1, "bx": 0.5, "by": 0.9}


def make_flow(**overrides) -> FlowCounter:
    settings = {"line": dict(LINE)}
    settings.update(overrides)
    return FlowCounter(settings)


def det(cx: float, cy: float, w: float = 0.08, h: float = 0.08) -> dict:
    """归一化中心坐标 → 检测框。"""
    return {
        "classId": "object",
        "className": "物体",
        "confidence": 0.9,
        "box": {"x": cx - w / 2, "y": cy - h / 2, "width": w, "height": h},
    }


def run_frames(flow: FlowCounter, frames: list[list[dict]]) -> tuple[list[dict], list[dict]]:
    """按帧序列驱动；返回 (全部计数事件, 最后一帧的输出)。"""
    events: list[dict] = []
    output: dict = {}
    for seq, frame in enumerate(frames):
        output = flow.update(frame, frame_sequence=seq)
        events.extend(output["countingEvents"])
    return events, output


def events_of(events: list[dict], type_: str) -> list[dict]:
    return [e for e in events if e["type"] == type_]


# ---- 动态去重（unique，CNT-D） ------------------------------------------


def test_stationary_single_appeared():
    flow = make_flow(countingMode="unique")
    events, output = run_frames(flow, [[det(0.3, 0.5)] for _ in range(30)])
    assert len(events_of(events, "appeared")) == 1
    assert output["metrics"][0]["value"] == 1


def test_moving_target_single_id_and_appeared():
    """CNT-D-001：目标横穿画面，累计 1，Track ID 全程不变。"""
    flow = make_flow(countingMode="unique")
    events, _ = run_frames(flow, [[det(0.1 + 0.02 * i, 0.5)] for i in range(41)])
    assert len(events_of(events, "appeared")) == 1


def test_occlusion_recovery_same_target_no_recount():
    """CNT-D-003：遮挡 5 帧（< maxLostFrames 8）后重现，保持 ID 不重复累计。"""
    flow = make_flow(countingMode="unique")
    frames = [[det(0.4, 0.5)]] * 10 + [[]] * 5 + [[det(0.4, 0.5)]] * 10
    events, output = run_frames(flow, frames)
    assert len(events_of(events, "appeared")) == 1
    assert output["metrics"][0]["value"] == 1


def test_reentry_within_dedup_window_not_recounted():
    """CNT-D-004 既定政策：去重窗口内原位复现视为同一目标，不重复累计。"""
    flow = make_flow(countingMode="unique", maxLostFrames=8, dedupLostFrames=25)
    # 丢失 20 帧：轨迹在第 9 帧移入去重记忆，复现时距移除 11 帧 < 窗口 25
    frames = [[det(0.4, 0.5)]] * 10 + [[]] * 20 + [[det(0.4, 0.5)]] * 10
    events, _ = run_frames(flow, frames)
    assert len(events_of(events, "appeared")) == 1


def test_reentry_beyond_window_counted_as_new():
    """CNT-D-004：超出去重窗口后返回，按新目标计（测试报告注明政策）。"""
    flow = make_flow(countingMode="unique", maxLostFrames=8, dedupLostFrames=25)
    frames = [[det(0.4, 0.5)]] * 10 + [[]] * 60 + [[det(0.4, 0.5)]] * 10
    events, _ = run_frames(flow, frames)
    assert len(events_of(events, "appeared")) == 2


def test_retirement_does_not_disturb_live_tracks():
    """回归：一条轨迹超限淘汰时，画面内其他活跃轨迹必须保持 ID 与确认状态。

    （曾因重建列表条件写反，淘汰发生时误删全部已匹配轨迹，导致重复累计。）
    """
    flow = make_flow(countingMode="unique", maxLostFrames=8, dedupLostFrames=25)
    frames = (
        [[det(0.2, 0.5), det(0.8, 0.5)]] * 10   # 双目标确认
        + [[det(0.2, 0.5)]] * 12                 # 右侧目标消失 → 超限淘汰
        + [[det(0.2, 0.5)]] * 10                 # 左侧目标持续在场
    )
    events, output = run_frames(flow, frames)
    assert len(events_of(events, "appeared")) == 2
    assert output["metrics"][0]["value"] == 1
    assert len(output["tracks"]) == 1


def test_short_false_positive_filtered():
    """CNT-D-006：仅出现 1~2 帧的假目标不达 minHits，不产生累计事件。"""
    flow = make_flow(countingMode="unique", minHits=3)
    frames = (
        [[]] * 5
        + [[det(0.7, 0.2)]]  # 1 帧闪现
        + [[]] * 12  # 移入记忆（never confirmed）
        + [[det(0.6, 0.3)], [det(0.61, 0.3)]]  # 2 帧闪现
        + [[]] * 12
    )
    events, _ = run_frames(flow, frames)
    assert events == []


def test_two_targets_crossing_keep_ids():
    """CNT-D-005：两目标交叉运动，不换 ID、累计恰为 2。

    判定方式：输出轨迹的 trail 首尾 x 方向应各自保持单调（左→右 / 右→左）。
    """
    flow = make_flow(countingMode="unique")
    frames = [
        [det(0.1 + 0.015 * i, 0.35 + 0.007 * i), det(0.9 - 0.015 * i, 0.65 - 0.007 * i)]
        for i in range(41)
    ]
    events, output = run_frames(flow, frames)
    assert len(events_of(events, "appeared")) == 2
    directions = []
    for track in output["tracks"]:
        dx = track["trail"][-1]["x"] - track["trail"][0]["x"]
        directions.append(math.copysign(1, dx))
    assert sorted(directions) == [-1.0, 1.0]  # 一去一回，无 ID 交换迹象
    ids = {track["trackId"] for track in output["tracks"]}
    assert len(ids) == 2


def test_reset_session_recounts():
    """会话重建/清零后，同一目标再次出现应重新累计（CNT-L-012 配套）。"""
    flow = make_flow(countingMode="unique")
    events, _ = run_frames(flow, [[det(0.4, 0.5)]] * 10)
    assert len(events_of(events, "appeared")) == 1
    flow.reset_session()
    events, _ = run_frames(flow, [[det(0.4, 0.5)]] * 10)
    assert len(events_of(events, "appeared")) == 1


# ---- 跨线计数（line，CNT-L） --------------------------------------------


def test_line_forward_cross_counts_once():
    """CNT-L-001：左→右完整通过，正向 +1、仅一条 CrossedLine。"""
    flow = make_flow(countingMode="line")
    events, _ = run_frames(flow, [[det(0.1 + 0.025 * i, 0.5)] for i in range(33)])
    crossed = events_of(events, "crossedLine")
    assert len(crossed) == 1
    assert crossed[0]["direction"] == "forward"
    assert crossed[0]["delta"] == 1
    assert crossed[0]["trackId"]


def test_line_reverse_cross_counts_reverse():
    """CNT-L-002：右→左通过，反向 +1。"""
    flow = make_flow(countingMode="line")
    events, _ = run_frames(flow, [[det(0.9 - 0.025 * i, 0.5)] for i in range(33)])
    crossed = events_of(events, "crossedLine")
    assert len(crossed) == 1
    assert crossed[0]["direction"] == "reverse"


def test_line_jitter_in_band_no_events():
    """CNT-L-003：检测线附近带内抖动不产生任何事件。"""
    flow = make_flow(countingMode="line", hysteresis=0.02)
    frames = [[det(0.5 + 0.012 * math.sin(0.7 * i), 0.5)] for i in range(60)]
    events, _ = run_frames(flow, frames)
    assert events == []


def test_line_foldback_immediately_no_count():
    """CNT-L-004：越过线进入对侧确认区但未连续停留 farConfirmFrames 帧即折返，不计。

    帧间步长 0.02 保持轨迹连续（框 IoU ≥ 0.3），确保检验的是滞回状态机而非轨迹丢失。
    """
    flow = make_flow(countingMode="line", hysteresis=0.03, farConfirmFrames=2)
    frames = (
        [[det(0.30 + 0.02 * i, 0.5)] for i in range(5)]    # 0.30..0.38 确立 - 侧
        + [[det(0.40 + 0.02 * i, 0.5)] for i in range(8)]  # 0.40..0.54：仅 1 帧入 + 侧确认区
        + [[det(0.52 - 0.02 * i, 0.5)] for i in range(12)] # 0.52..0.30 折返原侧
    )
    events, _ = run_frames(flow, frames)
    assert events == []


def test_line_round_trip_forward_then_reverse():
    """完整去而复返 = 正向 1 + 反向 1（既定政策：对侧确认后穿越成立）。"""
    flow = make_flow(countingMode="line", hysteresis=0.02, farConfirmFrames=2)
    frames = (
        [[det(0.28 + 0.03 * i, 0.5)] for i in range(5)]    # 0.28..0.40（- 侧）
        + [[det(0.43 + 0.03 * i, 0.5)] for i in range(8)]  # 0.43..0.64 穿越（+ 侧确认）
        + [[det(0.61 - 0.03 * i, 0.5)] for i in range(12)] # 0.61..0.28 穿回
    )
    events, _ = run_frames(flow, frames)
    crossed = events_of(events, "crossedLine")
    assert [e["direction"] for e in crossed] == ["forward", "reverse"]


def test_parallel_targets_both_counted():
    """CNT-L-006：两目标并排通过，各产生一条事件且 TrackId 不同。"""
    flow = make_flow(countingMode="line")
    frames = [
        [det(0.1 + 0.025 * i, 0.3), det(0.1 + 0.025 * i, 0.7)]
        for i in range(33)
    ]
    events, _ = run_frames(flow, frames)
    crossed = events_of(events, "crossedLine")
    assert len(crossed) == 2
    assert len({e["trackId"] for e in crossed}) == 2
    assert all(e["direction"] == "forward" for e in crossed)


# ---- 参数校验与清单 -------------------------------------------------------


def test_invalid_settings_rejected():
    with pytest.raises(ValueError):
        make_flow(countingMode="zone")  # 未支持的模式
    with pytest.raises(ValueError):
        make_flow(countingMode="line", line={"ax": 0.5, "ay": 0.5, "bx": 0.5, "by": 0.5})
    with pytest.raises(ValueError):
        make_flow(farConfirmFrames=0)
    with pytest.raises(ValueError):
        make_flow(hysteresis=0.5)


def test_manifest_is_valid():
    manifest = json.loads((PLUGIN_DIR / "plugin.json").read_text(encoding="utf-8"))
    assert manifest["id"] == "com.vision.sample-flow-counter"
    assert manifest["protocolVersion"] == "1.0"
    assert manifest["runtime"]["entry"] == "worker.py"
    assert manifest["capabilities"]["stateful"] is True
    schema_keys = set(
        json.loads((PLUGIN_DIR / "settings.schema.json").read_text(encoding="utf-8"))["properties"]
    )
    # 检测层参数属于 SimpleCounter（由其自行合并默认值），schema 为超集
    assert set(DEFAULT_SETTINGS) <= schema_keys
    assert {"minAreaPx", "thresholdBlock"} <= schema_keys


# ---- 集成：真实检测 → 跟踪 → 事件（图片文件路径） ---------------------------


def _circle_frame(cx: float, size: tuple[int, int] = (800, 400), radius: int = 26) -> np.ndarray:
    width, height = size
    image = np.full((height, width, 3), 108, dtype=np.uint8)
    rng = np.random.default_rng(3)
    noise = rng.normal(0, 2, image.shape).astype(np.int16)
    image = np.clip(image.astype(np.int16) + noise, 0, 255).astype(np.uint8)
    cv2.circle(image, (int(cx * width), height // 2), radius, (215, 215, 215), -1)
    return image


@pytest.fixture(scope="module")
def tmp_dir(tmp_path_factory):
    return tmp_path_factory.mktemp("flow")


def test_full_pipeline_image_sequence(tmp_dir):
    """SimpleCounter 检测 → 跟踪 → 跨线事件：单目标左→右 = forward 1。"""
    flow = make_flow(countingMode="line", minAreaPx=500)
    events: list[dict] = []
    last = {}
    for i in range(33):
        path = tmp_dir / f"f{i:03d}.png"
        cv2.imencode(".png", _circle_frame(0.1 + 0.025 * i))[1].tofile(str(path))
        last = flow.process(str(path), frame_sequence=i)
        events.extend(last["countingEvents"])
    crossed = events_of(events, "crossedLine")
    assert len(crossed) == 1 and crossed[0]["direction"] == "forward"
    # 输出契约：camelCase 字段 + 检测框带 trackId + 轨迹/性能
    assert last["detections"] and "trackId" in last["detections"][0]
    assert last["tracks"] and last["tracks"][0]["trail"]
    assert "eventId" in crossed[0] and "counterId" in crossed[0]
    assert last["performance"]["totalMs"] >= 0


def test_image_sequence_reset_clears_memory(tmp_dir):
    """counter reset（会话重建）后，同位置目标再次确认会重新累计。"""
    flow = make_flow(countingMode="unique", minAreaPx=500)
    total = 0
    for round_ in range(2):
        for i in range(12):
            path = tmp_dir / f"r{round_}_{i:02d}.png"
            cv2.imencode(".png", _circle_frame(0.3))[1].tofile(str(path))
            result = flow.process(str(path), frame_sequence=i)
            total += len(events_of(result["countingEvents"], "appeared"))
        flow.reset_session()
    assert total == 2
