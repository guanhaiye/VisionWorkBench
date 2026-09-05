"""YOLO11 推理适配。

支持三种输出模式：

* detect：YOLO11 检测模型，输出目标框；
* instance：YOLO11-seg，输出每个实例的框和轮廓；
* semantic：优先读取原生 semantic_mask；对 YOLO11-seg 则按类别合并实例
  mask，生成语义区域轮廓和可选的类别掩膜图。

本模块只在 Worker 初始化时导入 ultralytics，协议/配置单元测试不需要安装
PyTorch。模型权重不随仓库提交，需在插件配置中指定本地权重文件。
"""

from __future__ import annotations

import time
import uuid
from pathlib import Path
from typing import Any, Iterable

import cv2
import numpy as np

from temporal_pose import TemporalPoseClassifier


SUPPORTED_TASKS = {"detect", "pose", "instance", "semantic"}


class Yolo11Settings:
    def __init__(self, values: dict[str, Any] | None, plugin_dir: Path) -> None:
        source = dict(values or {})
        task = str(source.get("task", "detect")).strip().lower()
        if task not in SUPPORTED_TASKS:
            raise ValueError("task 必须是 detect、instance 或 semantic")

        model_path = str(source.get("modelPath", "")).strip()
        if not model_path:
            model_path = (
                "models/yolo11n-pose.pt" if task == "pose"
                else "models/yolo11n.pt" if task == "detect"
                else "models/yolo11n-seg.pt"
            )
        resolved_model = Path(model_path)
        if not resolved_model.is_absolute():
            resolved_model = plugin_dir / resolved_model
        # 行为训练生成的 best.pt 是 TorchScript 时序模型，不能交给
        # ultralytics.YOLO 作为检测/姿态主模型。旧任务可能把它误存到了
        # modelPath，这里做一次运行时兜底，避免任务启动直接失败。
        if _is_behavior_model_path(resolved_model) or _is_legacy_behavior_model_path(resolved_model, plugin_dir):
            fallback_name = (
                "yolo11n-pose.pt" if task == "pose"
                else "yolo11n.pt" if task == "detect"
                else "yolo11n-seg.pt"
            )
            fallback = plugin_dir / "models" / fallback_name
            if fallback.is_file():
                resolved_model = fallback
        if not resolved_model.is_file():
            raise FileNotFoundError(f"YOLO11 模型文件不存在: {resolved_model}")

        self.task = task
        self.model_path = resolved_model
        self.confidence = _float_setting(source, "confidence", 0.25, 0.0, 1.0)
        self.iou = _float_setting(source, "iou", 0.7, 0.0, 1.0)
        self.image_size = _int_setting(source, "imageSize", 640, 32, 4096)
        self.max_detections = _int_setting(source, "maxDetections", 300, 1, 10000)
        self.device = str(source.get("device", "auto")).strip().lower() or "auto"
        if self.device not in {"auto", "cpu", "cuda", "cuda:0", "cuda:1"}:
            raise ValueError("device 仅支持 auto、cpu、cuda、cuda:0 或 cuda:1")
        self.half = bool(source.get("half", False))
        self.tracking = bool(source.get("tracking", False))
        self.temporal_model_type = str(source.get("temporalModelType", "stgcn")).strip().lower()
        if self.temporal_model_type not in {"stgcn", "posec3d"}:
            raise ValueError("temporalModelType must be stgcn or posec3d")
        self.temporal_sequence_length = _int_setting(source, "temporalSequenceLength", 30, 8, 300)
        temporal_model_path = str(source.get("temporalModelPath", "")).strip()
        resolved_temporal = Path(temporal_model_path) if temporal_model_path else None
        if resolved_temporal is not None and not resolved_temporal.is_absolute():
            resolved_temporal = plugin_dir / resolved_temporal
        self.temporal_model_path = resolved_temporal
        self.save_masks = bool(source.get("saveMasks", False))
        mask_directory = Path(str(source.get("maskDirectory", "outputs/masks")))
        self.mask_directory = (
            mask_directory if mask_directory.is_absolute() else plugin_dir / mask_directory
        )
        ignored = source.get("ignoredClassIds", [])
        if not isinstance(ignored, list):
            raise ValueError("ignoredClassIds 必须是整数数组")
        try:
            self.ignored_class_ids = {int(value) for value in ignored}
        except (TypeError, ValueError) as error:
            raise ValueError("ignoredClassIds 必须是整数数组") from error
    def as_dict(self) -> dict[str, Any]:
        return {
            "task": self.task,
            "modelPath": str(self.model_path),
            "confidence": self.confidence,
            "iou": self.iou,
            "imageSize": self.image_size,
            "maxDetections": self.max_detections,
            "device": self.device,
            "half": self.half,
            "tracking": self.tracking,
            "temporalModelType": self.temporal_model_type,
            "temporalSequenceLength": self.temporal_sequence_length,
            "temporalModelPath": str(self.temporal_model_path) if self.temporal_model_path else "",
            "saveMasks": self.save_masks,
            "maskDirectory": str(self.mask_directory),
            "ignoredClassIds": sorted(self.ignored_class_ids),
        }


def _is_behavior_model_path(path: Path) -> bool:
    return any(part.lower() == "behavior-models" for part in path.parts)


def _is_legacy_behavior_model_path(path: Path, plugin_dir: Path) -> bool:
    # 旧版本曾把行为训练的 best.pt 复制到插件 models 目录。
    return (
        path.name.lower() == "best.pt"
        and path.parent.resolve() == (plugin_dir / "models").resolve()
    )


class Yolo11Engine:
    def __init__(self, settings: dict[str, Any] | None, plugin_dir: Path | None = None) -> None:
        self.plugin_dir = (plugin_dir or Path(__file__).resolve().parent).resolve()
        self.settings = Yolo11Settings(settings, self.plugin_dir)

        try:
            from ultralytics import YOLO  # type: ignore
        except ImportError as error:
            raise RuntimeError(
                "未安装 ultralytics。请按插件 requirements.txt 安装 YOLO11 运行时"
            ) from error

        self.device = self._resolve_device(self.settings.device)
        model_suffix = self.settings.model_path.suffix.lower()
        task_hint = (
            "segment" if self.settings.task in {"instance", "semantic"}
            else "pose" if self.settings.task == "pose"
            else "detect"
        )
        self.model = YOLO(str(self.settings.model_path), task=task_hint) if model_suffix == ".engine" else YOLO(str(self.settings.model_path))
        self.temporal_pose = TemporalPoseClassifier(
            self.settings.temporal_model_type,
            self.settings.temporal_model_path,
            self.settings.temporal_sequence_length,
        ) if self.settings.task == "pose" else None
        model_task = str(getattr(self.model, "task", "")).lower()
        if self.settings.task == "instance" and model_task not in {"segment", ""}:
            raise ValueError("instance 模式必须使用 YOLO11-seg 模型")
        if self.settings.task == "semantic" and model_task not in {"segment", "semantic", ""}:
            raise ValueError("semantic 模式需要 YOLO11-seg 或兼容 semantic_mask 的模型")
        if self.settings.task == "pose" and model_task not in {"pose", ""}:
            raise ValueError("pose 模式必须使用 YOLO11-pose 模型")

        # 把 CUDA 上下文、模型迁移和 Ultralytics 首次编译开销放到初始化阶段，
        # 避免连续检测的第一帧承担冷启动延迟。
        self._warmup()

    def _warmup(self) -> None:
        image = np.zeros(
            (self.settings.image_size, self.settings.image_size, 3), dtype=np.uint8
        )
        self.model.predict(source=image, **self._predict_options())
        if self.device.startswith("cuda"):
            import torch  # type: ignore

            torch.cuda.synchronize()

    @staticmethod
    def _resolve_device(configured: str) -> str:
        try:
            import torch  # type: ignore
        except ImportError:
            if configured.startswith("cuda"):
                raise RuntimeError(
                    "已选择 CUDA，但 YOLO11 虚拟环境未安装 PyTorch。请安装 CUDA 版 PyTorch，或在任务配置中选择 CPU。"
                )
            return "cpu"

        if configured == "auto":
            return "cuda:0" if torch.cuda.is_available() else "cpu"
        if configured.startswith("cuda") and not torch.cuda.is_available():
            raise RuntimeError(
                "已选择 CUDA，但当前 YOLO11 虚拟环境不可用 CUDA。"
                f" torch.cuda.is_available()={torch.cuda.is_available()}，"
                f"torch.cuda.device_count()={torch.cuda.device_count()}。"
                "请安装 CUDA 版 PyTorch，或在任务配置中选择 CPU。"
            )
        return configured

    def predict(
        self,
        image_path: str,
        sequence: int = 0,
        roi: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        image = _read_image(image_path)
        full_height, full_width = image.shape[:2]
        roi_bounds = _roi_pixel_bounds(roi, full_width, full_height)
        inference_image = image
        if roi_bounds is not None:
            left, top, right, bottom = roi_bounds
            inference_image = image[top:bottom, left:right]
        height, width = inference_image.shape[:2]
        started = time.perf_counter()
        if self.settings.tracking:
            results = self.model.track(
                source=inference_image,
                persist=True,
                tracker="bytetrack.yaml",
                **self._predict_options(),
            )
        else:
            results = self.model.predict(source=inference_image, **self._predict_options())
        if not results:
            result: Any = None
        else:
            result = results[0]

        output = self._build_output(result, width, height, image_path, sequence)
        output["imageWidth"] = width
        output["imageHeight"] = height
        if roi_bounds is not None:
            _restore_output_coordinates(output, roi_bounds, full_width, full_height)
        total_ms = (time.perf_counter() - started) * 1000.0
        output["performance"] = {
            "inferenceMs": round(total_ms, 2),
            "totalMs": round(total_ms, 2),
            "device": self.device,
        }
        return output

    def _predict_options(self) -> dict[str, Any]:
        options: dict[str, Any] = {
            "conf": self.settings.confidence,
            "iou": self.settings.iou,
            "imgsz": self.settings.image_size,
            "device": self.device,
            "max_det": self.settings.max_detections,
            "verbose": False,
        }
        if self.settings.half and self.device.startswith("cuda"):
            options["half"] = True
        return options

    def _build_output(
        self,
        result: Any,
        width: int,
        height: int,
        image_path: str,
        sequence: int,
    ) -> dict[str, Any]:
        if result is None:
            return {"detections": [], "segmentations": [], "metrics": [{"name": "count", "value": 0}]}

        names = getattr(result, "names", getattr(self.model, "names", {}))
        boxes = getattr(result, "boxes", None)
        xyxy = _as_numpy(getattr(boxes, "xyxy", None))
        confidence = _as_numpy(getattr(boxes, "conf", None)).reshape(-1)
        class_ids = _as_numpy(getattr(boxes, "cls", None)).reshape(-1)
        track_ids = _as_numpy(getattr(boxes, "id", None)).reshape(-1)
        detections: list[dict[str, Any]] = []
        tracks: list[dict[str, Any]] = []
        for index, box in enumerate(xyxy.reshape((-1, 4)) if xyxy.size else []):
            class_id = int(class_ids[index]) if index < len(class_ids) else 0
            score = float(confidence[index]) if index < len(confidence) else 0.0
            track_id = _track_id(track_ids, index)
            normalized = _normalized_box(box, width, height)
            detections.append(
                {
                    "classId": str(class_id),
                    "className": _class_name(names, class_id),
                    "confidence": round(score, 6),
                    "box": normalized,
                    **({"trackId": track_id} if track_id is not None else {}),
                }
            )
            if track_id is not None:
                tracks.append(
                    {
                        "trackId": track_id,
                        "classId": str(class_id),
                        "box": normalized,
                        "age": 1,
                        "timeSinceUpdate": 0,
                    }
                )

        keypoints = self._pose_keypoints(result, detections, width, height) if self.settings.task == "pose" else []
        segmentations: list[dict[str, Any]] = []
        if self.settings.task == "instance":
            segmentations = self._instance_segmentations(
                result, detections, width, height, image_path, sequence
            )
        elif self.settings.task == "semantic":
            segmentations = self._semantic_segmentations(
                result, detections, width, height, image_path, sequence
            )

        return {
            "detections": detections,
            "tracks": tracks,
            "keypoints": keypoints,
            "segmentations": segmentations,
            "metrics": [
                {"name": "count", "value": len(detections), "unit": "objects"},
                {"name": "segmentationCount", "value": len(segmentations), "unit": "regions"},
            ],
        }

    def _pose_keypoints(
        self,
        result: Any,
        detections: list[dict[str, Any]],
        width: int,
        height: int,
    ) -> list[dict[str, Any]]:
        keypoints = getattr(result, "keypoints", None)
        xy = _as_numpy(getattr(keypoints, "xy", None))
        confidence = _as_numpy(getattr(keypoints, "conf", None))
        if xy.ndim != 3:
            return []
        output: list[dict[str, Any]] = []
        for person_index in range(min(len(detections), xy.shape[0])):
            points: list[dict[str, float]] = []
            for point_index, point in enumerate(xy[person_index]):
                if len(point) < 2:
                    continue
                score = 1.0
                if confidence.ndim >= 2 and person_index < confidence.shape[0] and point_index < confidence.shape[1]:
                    score = float(confidence[person_index, point_index])
                points.append(
                    {
                        "x": round(max(0.0, min(float(point[0]) / width, 1.0)), 8),
                        "y": round(max(0.0, min(float(point[1]) / height, 1.0)), 8),
                        "confidence": round(score, 6),
                    }
                )
            if points:
                track_id = detections[person_index].get("trackId")
                temporal_pose = getattr(self, "temporal_pose", None)
                temporal = temporal_pose.update(track_id, points) if temporal_pose else {
                    "poseQuality": round(
                        sum(point["confidence"] > 0.3 for point in points) / 17.0, 6
                    )
                }
                output.append(
                    {
                        "trackId": track_id,
                        "classId": detections[person_index]["classId"],
                        "points": points,
                        **temporal,
                    }
                )
        return output

    def _instance_segmentations(
        self,
        result: Any,
        detections: list[dict[str, Any]],
        width: int,
        height: int,
        image_path: str,
        sequence: int,
    ) -> list[dict[str, Any]]:
        masks = getattr(result, "masks", None)
        if masks is None:
            return []
        polygons = getattr(masks, "xyn", None)
        mask_data = _as_numpy(getattr(masks, "data", None))
        output: list[dict[str, Any]] = []
        for index, detection in enumerate(detections):
            contour = _polygon_from_list(polygons, index) if polygons is not None else []
            mask = _mask_at(mask_data, index)
            if not contour and mask is not None:
                contour = _mask_contours(mask)
            if not contour:
                continue
            saved = self._save_mask(mask, image_path, sequence, index, "instance")
            output.append(
                {
                    "mode": "instance",
                    "classId": detection["classId"],
                    "confidence": detection["confidence"],
                    "contours": _protocol_contour_list([contour]),
                    "areaRatio": round(_contour_area_ratio(contour), 8),
                    "maskImagePath": saved,
                }
            )
        return output

    def _semantic_segmentations(
        self,
        result: Any,
        detections: list[dict[str, Any]],
        width: int,
        height: int,
        image_path: str,
        sequence: int,
    ) -> list[dict[str, Any]]:
        semantic_mask = getattr(result, "semantic_mask", None)
        class_map = _as_numpy(getattr(semantic_mask, "data", None))
        if class_map.ndim > 2:
            class_map = np.squeeze(class_map)

        if class_map.ndim == 2 and class_map.size:
            return self._semantic_from_class_map(class_map, image_path, sequence)

        # YOLO11-seg is an instance model. OR masks with the same class to get
        # a deterministic semantic projection for the semantic output mode.
        masks = getattr(result, "masks", None)
        mask_data = _as_numpy(getattr(masks, "data", None))
        if mask_data.ndim != 3 or not detections:
            return []
        grouped: dict[int, np.ndarray] = {}
        scores: dict[int, float] = {}
        for index, detection in enumerate(detections):
            mask = _mask_at(mask_data, index)
            if mask is None:
                continue
            class_id = int(detection["classId"])
            if class_id in self.settings.ignored_class_ids:
                continue
            grouped[class_id] = np.logical_or(grouped.get(class_id, np.zeros_like(mask, dtype=bool)), mask)
            scores[class_id] = max(scores.get(class_id, 0.0), float(detection["confidence"]))

        output: list[dict[str, Any]] = []
        for class_id, mask in sorted(grouped.items()):
            contours = _mask_contour_list(mask)
            if not contours:
                continue
            saved = self._save_mask(mask, image_path, sequence, class_id, "semantic")
            output.append(
                {
                    "mode": "semantic",
                    "classId": str(class_id),
                    "confidence": round(scores.get(class_id, 0.0), 6),
                    "contours": _protocol_contour_list(contours),
                    "areaRatio": round(float(mask.mean()), 8),
                    "maskImagePath": saved,
                }
            )
        return output

    def _semantic_from_class_map(
        self, class_map: np.ndarray, image_path: str, sequence: int
    ) -> list[dict[str, Any]]:
        output: list[dict[str, Any]] = []
        for raw_class_id in np.unique(class_map):
            class_id = int(raw_class_id)
            if class_id in self.settings.ignored_class_ids:
                continue
            mask = class_map == raw_class_id
            contours = _mask_contour_list(mask)
            if not contours:
                continue
            saved = self._save_mask(mask, image_path, sequence, class_id, "semantic")
            output.append(
                {
                    "mode": "semantic",
                    "classId": str(class_id),
                    "confidence": 1.0,
                    "contours": _protocol_contour_list(contours),
                    "areaRatio": round(float(mask.mean()), 8),
                    "maskImagePath": saved,
                }
            )
        return output

    def _save_mask(
        self,
        mask: np.ndarray | None,
        image_path: str,
        sequence: int,
        index: int,
        mode: str,
    ) -> str | None:
        if not self.settings.save_masks or mask is None:
            return None
        self.settings.mask_directory.mkdir(parents=True, exist_ok=True)
        filename = f"{Path(image_path).stem}-{sequence}-{mode}-{index}-{uuid.uuid4().hex[:8]}.png"
        path = self.settings.mask_directory / filename
        values = (np.asarray(mask, dtype=np.uint8) * 255).astype(np.uint8)
        if not cv2.imwrite(str(path), values):
            raise OSError(f"无法保存掩膜图: {path}")
        return str(path)


def _float_setting(values: dict[str, Any], key: str, default: float, low: float, high: float) -> float:
    try:
        value = float(values.get(key, default))
    except (TypeError, ValueError) as error:
        raise ValueError(f"{key} 必须是数字") from error
    if not low <= value <= high:
        raise ValueError(f"{key} 必须在 {low}~{high} 范围内")
    return value


def _int_setting(values: dict[str, Any], key: str, default: int, low: int, high: int) -> int:
    try:
        value = int(values.get(key, default))
    except (TypeError, ValueError) as error:
        raise ValueError(f"{key} 必须是整数") from error
    if not low <= value <= high:
        raise ValueError(f"{key} 必须在 {low}~{high} 范围内")
    return value


def _read_image(image_path: str) -> np.ndarray:
    data = np.fromfile(image_path, dtype=np.uint8)
    if data.size == 0:
        raise FileNotFoundError(f"图片不存在或为空: {image_path}")
    image = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f"图片解码失败: {image_path}")
    return image


def _as_numpy(value: Any) -> np.ndarray:
    if value is None:
        return np.asarray([])
    if hasattr(value, "detach"):
        value = value.detach()
    if hasattr(value, "cpu"):
        value = value.cpu()
    if hasattr(value, "numpy"):
        value = value.numpy()
    return np.asarray(value)


def _class_name(names: Any, class_id: int) -> str:
    if isinstance(names, dict):
        return str(names.get(class_id, names.get(str(class_id), class_id)))
    if isinstance(names, (list, tuple)) and 0 <= class_id < len(names):
        return str(names[class_id])
    return str(class_id)


def _track_id(values: np.ndarray, index: int) -> str | None:
    if index >= len(values):
        return None
    try:
        value = float(values[index])
    except (TypeError, ValueError):
        return None
    if not np.isfinite(value):
        return None
    return str(int(value)) if value.is_integer() else str(value)


def _normalized_box(box: Iterable[float], width: int, height: int) -> dict[str, float]:
    x1, y1, x2, y2 = [float(value) for value in box]
    x1, x2 = sorted((max(0.0, min(x1, width)), max(0.0, min(x2, width))))
    y1, y2 = sorted((max(0.0, min(y1, height)), max(0.0, min(y2, height))))
    return {
        "x": round(x1 / width, 8),
        "y": round(y1 / height, 8),
        "width": round((x2 - x1) / width, 8),
        "height": round((y2 - y1) / height, 8),
    }


def _roi_pixel_bounds(
    roi: dict[str, Any] | None, width: int, height: int
) -> tuple[int, int, int, int] | None:
    """Convert a normalized ROI to a non-empty pixel crop."""
    if not isinstance(roi, dict):
        return None
    try:
        x = float(roi.get("x", 0.0))
        y = float(roi.get("y", 0.0))
        roi_width = float(roi.get("width", 0.0))
        roi_height = float(roi.get("height", 0.0))
    except (TypeError, ValueError):
        return None
    if roi_width <= 0 or roi_height <= 0:
        return None
    x = max(0.0, min(x, 1.0))
    y = max(0.0, min(y, 1.0))
    x2 = max(x, min(x + roi_width, 1.0))
    y2 = max(y, min(y + roi_height, 1.0))
    left = max(0, min(width - 1, int(round(x * width))))
    top = max(0, min(height - 1, int(round(y * height))))
    right = max(left + 1, min(width, int(round(x2 * width))))
    bottom = max(top + 1, min(height, int(round(y2 * height))))
    return left, top, right, bottom


def _restore_output_coordinates(
    output: dict[str, Any], bounds: tuple[int, int, int, int], full_width: int, full_height: int
) -> None:
    """Map crop-relative output coordinates back to the original image."""
    left, top, right, bottom = bounds
    offset_x = left / full_width
    offset_y = top / full_height
    scale_x = (right - left) / full_width
    scale_y = (bottom - top) / full_height

    for detection in output.get("detections", []):
        _restore_box(detection.get("box"), offset_x, offset_y, scale_x, scale_y)
    for track in output.get("tracks", []):
        _restore_box(track.get("box"), offset_x, offset_y, scale_x, scale_y)
        for point in track.get("trail", []):
            _restore_point(point, offset_x, offset_y, scale_x, scale_y)
    for keypoint in output.get("keypoints", []):
        for point in keypoint.get("points", []):
            _restore_point(point, offset_x, offset_y, scale_x, scale_y)
    for segmentation in output.get("segmentations", []):
        for contour in segmentation.get("contours", []):
            for point in contour:
                if isinstance(point, list) and len(point) >= 2:
                    point[0] = offset_x + float(point[0]) * scale_x
                    point[1] = offset_y + float(point[1]) * scale_y
        if "areaRatio" in segmentation:
            segmentation["areaRatio"] = float(segmentation["areaRatio"]) * scale_x * scale_y


def _restore_box(
    box: dict[str, Any] | None, offset_x: float, offset_y: float, scale_x: float, scale_y: float
) -> None:
    if not isinstance(box, dict):
        return
    box["x"] = offset_x + float(box.get("x", 0.0)) * scale_x
    box["y"] = offset_y + float(box.get("y", 0.0)) * scale_y
    box["width"] = float(box.get("width", 0.0)) * scale_x
    box["height"] = float(box.get("height", 0.0)) * scale_y


def _restore_point(
    point: dict[str, Any] | None, offset_x: float, offset_y: float, scale_x: float, scale_y: float
) -> None:
    if not isinstance(point, dict):
        return
    point["x"] = offset_x + float(point.get("x", 0.0)) * scale_x
    point["y"] = offset_y + float(point.get("y", 0.0)) * scale_y


def _polygon_from_list(polygons: Any, index: int) -> list[list[float]]:
    try:
        polygon = np.asarray(polygons[index], dtype=np.float32)
    except (IndexError, TypeError, ValueError):
        return []
    return _normalize_polygon(polygon)


def _normalize_polygon(polygon: np.ndarray) -> list[list[float]]:
    if polygon.ndim != 2 or polygon.shape[1] < 2:
        return []
    return [[round(float(point[0]), 8), round(float(point[1]), 8)] for point in polygon]


def _mask_at(mask_data: np.ndarray, index: int) -> np.ndarray | None:
    if mask_data.ndim != 3 or index < 0 or index >= mask_data.shape[0]:
        return None
    return mask_data[index] > 0.5


def _mask_contours(mask: np.ndarray) -> list[list[float]]:
    contours = _mask_contour_list(mask)
    return contours[0] if contours else []


def _mask_contour_list(mask: np.ndarray) -> list[list[list[float]]]:
    binary = (np.asarray(mask, dtype=np.uint8) * 255).astype(np.uint8)
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return []
    height, width = binary.shape[:2]
    ordered = sorted(contours, key=cv2.contourArea, reverse=True)
    return [
        [
            [round(float(x) / width, 8), round(float(y) / height, 8)]
            for x, y in contour.reshape(-1, 2)
        ]
        for contour in ordered
    ]


def _contour_area_ratio(contour: list[list[float]]) -> float:
    if len(contour) < 3:
        return 0.0
    area = 0.0
    for index, (x1, y1) in enumerate(contour):
        x2, y2 = contour[(index + 1) % len(contour)]
        area += x1 * y2 - x2 * y1
    return abs(area) / 2.0


def _protocol_contour_list(
    contours: list[list[list[float]]],
) -> list[list[dict[str, float]]]:
    """把内部的 [x, y] 轮廓转换为宿主协议要求的 {x, y} 点。"""
    return [
        [
            {"x": point[0], "y": point[1]}
            for point in contour
            if len(point) >= 2
        ]
        for contour in contours
    ]
