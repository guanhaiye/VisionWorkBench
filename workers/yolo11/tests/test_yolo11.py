"""YOLO11 适配层测试，不加载真实 PyTorch 权重。"""

from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace

import numpy as np

PLUGIN_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PLUGIN_DIR))

from yolo11 import Yolo11Engine, Yolo11Settings, _mask_contours, _normalized_box  # noqa: E402


def _engine(tmp_path: Path, task: str) -> Yolo11Engine:
    (tmp_path / "model.pt").write_bytes(b"test-weight-placeholder")
    engine = object.__new__(Yolo11Engine)
    engine.settings = Yolo11Settings(
        {"task": task, "modelPath": "model.pt"}, tmp_path
    )
    engine.model = SimpleNamespace(names={0: "person", 1: "part"})
    return engine


def _result() -> SimpleNamespace:
    boxes = SimpleNamespace(
        xyxy=np.asarray([[10, 5, 30, 25]], dtype=np.float32),
        conf=np.asarray([0.91], dtype=np.float32),
        cls=np.asarray([1], dtype=np.float32),
    )
    masks = SimpleNamespace(
        xyn=[np.asarray([[0.1, 0.1], [0.3, 0.1], [0.3, 0.3], [0.1, 0.3]], dtype=np.float32)],
        data=np.asarray(
            [[[0, 0, 0, 0], [0, 1, 1, 0], [0, 1, 1, 0], [0, 0, 0, 0]]],
            dtype=np.float32,
        ),
    )
    return SimpleNamespace(names={0: "person", 1: "part"}, boxes=boxes, masks=masks)


def test_settings_validate_and_resolve_model_path(tmp_path: Path):
    (tmp_path / "weights.pt").write_bytes(b"weights")
    settings = Yolo11Settings(
        {"task": "semantic", "modelPath": "weights.pt", "confidence": 0.4},
        tmp_path,
    )
    assert settings.task == "semantic"
    assert settings.model_path == tmp_path / "weights.pt"
    assert settings.confidence == 0.4


def test_detection_output_is_normalized(tmp_path: Path):
    engine = _engine(tmp_path, "detect")
    output = engine._build_output(_result(), 100, 50, "input.png", 1)
    assert output["detections"][0]["classId"] == "1"
    assert output["detections"][0]["className"] == "part"
    assert output["detections"][0]["box"] == {
        "x": 0.1,
        "y": 0.1,
        "width": 0.2,
        "height": 0.4,
    }
    assert output["segmentations"] == []


def test_instance_output_contains_individual_mask(tmp_path: Path):
    engine = _engine(tmp_path, "instance")
    output = engine._build_output(_result(), 100, 50, "input.png", 1)
    assert len(output["segmentations"]) == 1
    assert output["segmentations"][0]["mode"] == "instance"
    assert output["segmentations"][0]["classId"] == "1"
    assert len(output["segmentations"][0]["contours"][0]) == 4


def test_semantic_output_merges_same_class_masks(tmp_path: Path):
    engine = _engine(tmp_path, "semantic")
    result = _result()
    result.boxes = SimpleNamespace(
        xyxy=np.asarray([[0, 0, 2, 2], [2, 2, 4, 4]], dtype=np.float32),
        conf=np.asarray([0.8, 0.9], dtype=np.float32),
        cls=np.asarray([1, 1], dtype=np.float32),
    )
    result.masks.data = np.asarray(
        [
            [[1, 1, 0, 0], [1, 1, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]],
            [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 1, 1], [0, 0, 1, 1]],
        ],
        dtype=np.float32,
    )
    result.masks.xyn = None
    output = engine._build_output(result, 4, 4, "input.png", 1)
    assert len(output["segmentations"]) == 1
    assert output["segmentations"][0]["mode"] == "semantic"
    assert output["segmentations"][0]["areaRatio"] == 0.5
    assert output["segmentations"][0]["contours"]


def test_mask_helpers_are_safe_for_empty_input():
    assert _normalized_box([-5, 2, 110, 60], 100, 50) == {
        "x": 0.0,
        "y": 0.04,
        "width": 1.0,
        "height": 0.96,
    }
    assert _mask_contours(np.zeros((4, 4), dtype=bool)) == []
