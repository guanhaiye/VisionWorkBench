"""sample-counter 单元测试：合成图片上的计数正确性。"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np
import pytest

PLUGIN_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PLUGIN_DIR))

from counter import SimpleCounter, DEFAULT_SETTINGS  # noqa: E402


def make_image(count: int, seed: int = 7, size: tuple[int, int] = (640, 480)) -> np.ndarray:
    """灰底 + count 个亮色圆形/矩形（保证互不粘连）。"""
    rng = np.random.default_rng(seed)
    width, height = size
    image = np.full((height, width, 3), 110, dtype=np.uint8)
    noise = rng.normal(0, 4, image.shape).astype(np.int16)
    image = np.clip(image.astype(np.int16) + noise, 0, 255).astype(np.uint8)
    placed: list[tuple[int, int, int]] = []  # (cx, cy, effective_radius)

    def try_place() -> tuple[int, int, int, bool] | None:
        for _ in range(200):
            radius = int(rng.integers(14, 30))
            cx = int(rng.integers(radius + 12, width - radius - 12))
            cy = int(rng.integers(radius + 12, height - radius - 12))
            reach = int(radius * 1.7)  # 矩形对角
            ok = all(
                (cx - px) ** 2 + (cy - py) ** 2 > (reach + pr + 10) ** 2
                for px, py, pr in placed
            )
            if ok:
                return cx, cy, radius, radius
        return None

    for i in range(count):
        spot = try_place()
        if spot is None:
            raise AssertionError(f"无法无重叠放置 {count} 个目标")
        cx, cy, radius, _ = spot
        placed.append((cx, cy, int(radius * 1.5)))
        color = (int(rng.integers(180, 240)),) * 3
        if i % 2 == 0:
            cv2.circle(image, (cx, cy), radius, color, -1)
        else:
            w, h = radius * 2, int(radius * 1.4)
            cv2.rectangle(image, (cx - w // 2, cy - h // 2), (cx + w // 2, cy + h // 2), color, -1)
    return image


@pytest.fixture(scope="module")
def tmp_dir(tmp_path_factory):
    return tmp_path_factory.mktemp("counter")


def run_counter(image: np.ndarray, path: Path, settings=None) -> dict:
    cv2.imencode(".png", image)[1].tofile(str(path))
    counter = SimpleCounter(settings)
    return counter.process(str(path))


@pytest.mark.parametrize("count", [0, 1, 2, 5, 10, 20, 40])
def test_exact_count(tmp_dir, count):
    size = (1280, 960) if count > 25 else (640, 480)
    image = make_image(count, seed=count + 1, size=size)
    result = run_counter(image, tmp_dir / f"n{count}.png")
    detected = result["metrics"][0]["value"]
    assert detected == count, f"真值 {count}，检测 {detected}"


def test_empty_scene_no_false_positive(tmp_dir):
    result = run_counter(make_image(0), tmp_dir / "empty.png")
    assert result["metrics"][0]["value"] == 0
    assert result["detections"] == []


def test_normalized_boxes_in_range(tmp_dir):
    result = run_counter(make_image(6), tmp_dir / "boxes.png")
    for det in result["detections"]:
        box = det["box"]
        assert 0.0 <= box["x"] and box["x"] + box["width"] <= 1.0 + 1e-6
        assert 0.0 <= box["y"] and box["y"] + box["height"] <= 1.0 + 1e-6
        assert det["confidence"] > 0.5


def test_invalid_settings_rejected():
    with pytest.raises(ValueError):
        SimpleCounter({"thresholdBlock": 30})  # 偶数
    with pytest.raises(ValueError):
        SimpleCounter({"minAreaPx": 0})


def test_corrupt_file_rejected(tmp_dir):
    bad = tmp_dir / "fake.jpg"
    bad.write_bytes(b"this is not an image at all")
    with pytest.raises(ValueError):
        SimpleCounter().process(str(bad))


def test_manifest_is_valid():
    manifest = json.loads((PLUGIN_DIR / "plugin.json").read_text(encoding="utf-8"))
    assert manifest["id"] == "com.vision.sample-counter"
    assert manifest["protocolVersion"] == "1.0"
    assert manifest["runtime"]["entry"] == "worker.py"
    schema_keys = set(
        json.loads((PLUGIN_DIR / "settings.schema.json").read_text(encoding="utf-8"))["properties"]
    )
    assert set(DEFAULT_SETTINGS) == schema_keys
