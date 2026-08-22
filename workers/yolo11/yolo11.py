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


SUPPORTED_TASKS = {"detect", "instance", "semantic"}


class Yolo11Settings:
    def __init__(self, values: dict[str, Any] | None, plugin_dir: Path) -> None:
        source = dict(values or {})
        task = str(source.get("task", "detect")).strip().lower()
        if task not in SUPPORTED_TASKS:
            raise ValueError("task 必须是 detect、instance 或 semantic")

        model_path = str(source.get("modelPath", "")).strip()
        if not model_path:
            model_path = "models/yolo11n.pt" if task == "detect" else "models/yolo11n-seg.pt"
        resolved_model = Path(model_path)
        if not resolved_model.is_absolute():
            resolved_model = plugin_dir / resolved_model
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
            "saveMasks": self.save_masks,
            "maskDirectory": str(self.mask_directory),
            "ignoredClassIds": sorted(self.ignored_class_ids),
        }


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
        self.model = YOLO(str(self.settings.model_path))
        model_task = str(getattr(self.model, "task", "")).lower()
        if self.settings.task == "instance" and model_task not in {"segment", ""}:
            raise ValueError("instance 模式必须使用 YOLO11-seg 模型")
        if self.settings.task == "semantic" and model_task not in {"segment", "semantic", ""}:
            raise ValueError("semantic 模式需要 YOLO11-seg 或兼容 semantic_mask 的模型")

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

    def predict(self, image_path: str, sequence: int = 0) -> dict[str, Any]:
        image = _read_image(image_path)
        height, width = image.shape[:2]
        started = time.perf_counter()
        results = self.model.predict(
            source=image_path,
            conf=self.settings.confidence,
            iou=self.settings.iou,
            imgsz=self.settings.image_size,
            device=self.device,
            half=self.settings.half and self.device.startswith("cuda"),
            max_det=self.settings.max_detections,
            verbose=False,
        )
        if not results:
            result: Any = None
        else:
            result = results[0]

        output = self._build_output(result, width, height, image_path, sequence)
        total_ms = (time.perf_counter() - started) * 1000.0
        output["performance"] = {
            "inferenceMs": round(total_ms, 2),
            "totalMs": round(total_ms, 2),
            "device": self.device,
        }
        return output

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
        detections: list[dict[str, Any]] = []
        for index, box in enumerate(xyxy.reshape((-1, 4)) if xyxy.size else []):
            class_id = int(class_ids[index]) if index < len(class_ids) else 0
            score = float(confidence[index]) if index < len(confidence) else 0.0
            detections.append(
                {
                    "classId": str(class_id),
                    "className": _class_name(names, class_id),
                    "confidence": round(score, 6),
                    "box": _normalized_box(box, width, height),
                }
            )

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
            "segmentations": segmentations,
            "metrics": [
                {"name": "count", "value": len(detections), "unit": "objects"},
                {"name": "segmentationCount", "value": len(segmentations), "unit": "regions"},
            ],
        }

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
                    "contours": [contour],
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
                    "contours": contours,
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
                    "contours": contours,
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
