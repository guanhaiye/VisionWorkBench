"""Batch YOLO tester that loads the model exactly once."""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path


def emit(values: dict) -> None:
    print(json.dumps(values, ensure_ascii=False), flush=True)


def main() -> int:
    try:
        request = json.loads(sys.stdin.read().lstrip("\ufeff").strip())
        from ultralytics import YOLO
        import torch
        from test_worker import read_model_image_size

        model_path = Path(str(request["modelPath"])).resolve()
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
        device = str(request.get("device", "auto"))
        if device == "auto":
            device = "0" if torch.cuda.is_available() else "cpu"
        image_size = read_model_image_size(model_path, model)

        for image_value in request.get("imagePaths", []):
            image_path = Path(str(image_value)).resolve()
            try:
                started = time.perf_counter()
                result = model.predict(source=str(image_path), conf=float(request.get("confidence", .25)),
                                       iou=float(request.get("iou", .7)), device=device, imgsz=image_size,
                                       verbose=False)[0]
                elapsed_ms = (time.perf_counter() - started) * 1000
                names, detections = result.names or {}, []
                if result.boxes is not None:
                    xyxyn = result.boxes.xyxyn.detach().cpu().numpy()
                    classes = result.boxes.cls.detach().cpu().numpy()
                    scores = result.boxes.conf.detach().cpu().numpy()
                    for index, box in enumerate(xyxyn):
                        class_id = int(classes[index])
                        name = names.get(class_id, str(class_id)) if isinstance(names, dict) else str(class_id)
                        detections.append({"classId": class_id, "className": str(name), "confidence": float(scores[index]),
                                           "x": float(box[0]), "y": float(box[1]), "width": float(box[2]-box[0]),
                                           "height": float(box[3]-box[1])})
                masks = []
                if task == "segment" and result.masks is not None:
                    for index, polygon in enumerate(result.masks.xyn):
                        if len(polygon) < 3:
                            continue
                        if len(polygon) > 300:
                            polygon = polygon[::max(1, len(polygon) // 300)]
                        detection = detections[index] if index < len(detections) else {}
                        masks.append({"classId": detection.get("classId", 0), "className": detection.get("className", "object"),
                                      "confidence": detection.get("confidence", 0.0),
                                      "polygon": [{"x": float(p[0]), "y": float(p[1])} for p in polygon]})
                emit({"event": "result", "imagePath": str(image_path), "success": True, "task": task,
                      "elapsedMs": elapsed_ms, "detections": detections, "masks": masks})
            except Exception as error:  # noqa: BLE001
                emit({"event": "result", "imagePath": str(image_path), "success": False, "error": str(error)})
        emit({"event": "completed", "success": True})
        return 0
    except Exception as error:  # noqa: BLE001
        emit({"event": "error", "success": False, "error": str(error)})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
