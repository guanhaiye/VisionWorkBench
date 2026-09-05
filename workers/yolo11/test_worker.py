"""One-shot YOLO model tester for detection and instance segmentation."""
from __future__ import annotations

import json
import sys
import time
from typing import Any
from pathlib import Path


def run(request: dict) -> dict:
    from ultralytics import YOLO
    import torch

    model_path = Path(str(request.get("modelPath", ""))).resolve()
    image_path = Path(str(request.get("imagePath", ""))).resolve()
    if not model_path.is_file():
        raise FileNotFoundError(f"模型不存在：{model_path}")
    if not image_path.is_file():
        raise FileNotFoundError(f"图片不存在：{image_path}")

    task_hint = None
    if model_path.suffix.lower() == ".engine":
        task_file = model_path.with_suffix(".engine.task")
        sibling_pt = model_path.with_suffix(".pt")
        if task_file.is_file():
            task_hint = task_file.read_text(encoding="utf-8").strip()
        elif sibling_pt.is_file():
            task_hint = str(getattr(YOLO(str(sibling_pt)), "task", "")).lower()
    model = YOLO(str(model_path), task=task_hint) if task_hint else YOLO(str(model_path))
    task = str(getattr(model, "task", "")).lower()
    if task not in {"detect", "segment"}:
        raise ValueError(f"当前测试平台仅支持 detect/segment，模型类型为：{task or 'unknown'}")

    started = time.perf_counter()
    device = str(request.get("device", "auto"))
    if device == "auto":
        device = "0" if torch.cuda.is_available() else "cpu"
    image_size = read_model_image_size(model_path, model)
    result = model.predict(
        source=str(image_path),
        conf=float(request.get("confidence", 0.25)),
        iou=float(request.get("iou", 0.7)),
        device=device,
        imgsz=image_size,
        verbose=False,
    )[0]
    elapsed_ms = (time.perf_counter() - started) * 1000
    names = result.names or {}
    boxes = result.boxes
    detections = []
    if boxes is not None:
        xyxyn = boxes.xyxyn.detach().cpu().numpy()
        classes = boxes.cls.detach().cpu().numpy()
        scores = boxes.conf.detach().cpu().numpy()
        for index, box in enumerate(xyxyn):
            class_id = int(classes[index])
            name = names.get(class_id, str(class_id)) if isinstance(names, dict) else str(class_id)
            detections.append({
                "classId": class_id, "className": str(name), "confidence": float(scores[index]),
                "x": float(box[0]), "y": float(box[1]),
                "width": float(box[2] - box[0]), "height": float(box[3] - box[1]),
            })

    masks = []
    if task == "segment" and result.masks is not None:
        for index, polygon in enumerate(result.masks.xyn):
            if len(polygon) < 3:
                continue
            # Limit contour density so the desktop overlay remains responsive.
            if len(polygon) > 300:
                step = max(1, len(polygon) // 300)
                polygon = polygon[::step]
            detection = detections[index] if index < len(detections) else {}
            masks.append({
                "classId": detection.get("classId", 0),
                "className": detection.get("className", "object"),
                "confidence": detection.get("confidence", 0.0),
                "polygon": [{"x": float(point[0]), "y": float(point[1])} for point in polygon],
            })
    return {"success": True, "task": task, "elapsedMs": elapsed_ms,
            "detections": detections, "masks": masks}


def read_model_image_size(model_path: Path, model: Any, default: int = 640) -> int:
    import torch

    if model_path.suffix.lower() == ".engine":
        metadata_path = model_path.with_suffix(".engine.meta.json")
        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            value = metadata.get("imageSize")
            if isinstance(value, (int, float)) and int(value) >= 32:
                return int(value)
        except (OSError, ValueError, TypeError):
            pass
    try:
        checkpoint = torch.load(model_path, map_location="cpu", weights_only=False)
        if isinstance(checkpoint, dict):
            metadata = checkpoint.get("visionWorkbench") or {}
            value = metadata.get("imageSize") if isinstance(metadata, dict) else None
            if isinstance(value, (int, float)) and int(value) >= 32:
                return int(value)
            args = checkpoint.get("train_args") or {}
            value = args.get("imgsz") if isinstance(args, dict) else None
            if isinstance(value, (list, tuple)):
                value = value[0] if value else None
            if isinstance(value, (int, float)) and int(value) >= 32:
                return int(value)
    except Exception:  # noqa: BLE001
        pass
    for source in (getattr(model, "overrides", None), getattr(getattr(model, "model", None), "args", None)):
        value = source.get("imgsz") if isinstance(source, dict) else None
        if isinstance(value, (list, tuple)):
            value = value[0] if value else None
        if isinstance(value, (int, float)) and int(value) >= 32:
            return int(value)
    return default


def main() -> int:
    try:
        payload = sys.stdin.read().lstrip("\ufeff").strip()
        if not payload:
            raise ValueError("测试进程没有收到请求数据")
        print(json.dumps(run(json.loads(payload)), ensure_ascii=False))
        return 0
    except Exception as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
