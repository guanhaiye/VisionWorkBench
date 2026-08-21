"""VisionWorkbench YOLOE visual-prompt smart annotation adapter.

Input and output are one JSON document on stdin/stdout.  The reference boxes
are normalized to the reference image and are passed to Ultralytics YOLOE as
visual prompts.  The same visual prompt is deliberately reused for every
target image, which provides cross-image propagation without drawing another
box.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(2)


def polygon_from_mask(mask):
    try:
        import cv2
        import numpy as np

        binary = (mask > 0.5).astype(np.uint8) * 255
        contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not contours:
            return []
        contour = max(contours, key=cv2.contourArea)
        epsilon = max(1.0, 0.002 * cv2.arcLength(contour, True))
        contour = cv2.approxPolyDP(contour, epsilon, True)
        height, width = binary.shape[:2]
        return [
            {"x": float(point[0][0] / max(1, width - 1)), "y": float(point[0][1] / max(1, height - 1))}
            for point in contour
        ]
    except Exception:
        return []


def main(payload: dict) -> dict:
    model_path = Path(payload.get("modelPath", ""))
    reference = Path(payload.get("referenceImage", ""))
    targets = [Path(path) for path in payload.get("targets", [])]
    prompts = payload.get("prompts", [])
    if not model_path.is_file():
        fail(f"YOLOE 模型不存在：{model_path}")
    if not reference.is_file():
        fail(f"参考图片不存在：{reference}")
    if not prompts:
        fail("YOLOE 至少需要一个矩形框作为视觉提示")
    if not targets:
        return {"results": []}

    try:
        import numpy as np
        from PIL import Image
        from ultralytics import YOLOE
        from ultralytics.models.yolo.yoloe.predict import YOLOEVPSegPredictor
    except ImportError as error:
        fail(f"YOLOE 运行环境不完整，请安装新版 ultralytics、numpy、Pillow：{error}")

    reference_width, reference_height = Image.open(reference).size
    bboxes = []
    class_ids = []
    for prompt in prompts:
        x = float(prompt["x"]) * reference_width
        y = float(prompt["y"]) * reference_height
        width = float(prompt["width"]) * reference_width
        height = float(prompt["height"]) * reference_height
        bboxes.append([x, y, x + width, y + height])
        class_ids.append(int(prompt.get("classId", len(class_ids))))

    model = YOLOE(str(model_path))
    visual_prompts = {"bboxes": np.asarray(bboxes, dtype=np.float32), "cls": np.asarray(class_ids, dtype=np.int64)}
    results = []
    for target in targets:
        if not target.is_file():
            continue
        predictions = model.predict(
            str(target),
            refer_image=str(reference),
            visual_prompts=visual_prompts,
            predictor=YOLOEVPSegPredictor,
            conf=float(payload.get("confidence", 0.25)),
            verbose=False,
        )
        objects = []
        for prediction in predictions:
            boxes = getattr(prediction, "boxes", None)
            masks = getattr(prediction, "masks", None)
            if boxes is None:
                continue
            xyxy = boxes.xyxy.cpu().numpy()
            classes = boxes.cls.cpu().numpy().astype(int)
            scores = boxes.conf.cpu().numpy() if getattr(boxes, "conf", None) is not None else [0.0] * len(xyxy)
            mask_values = masks.data.cpu().numpy() if masks is not None else []
            for index, box in enumerate(xyxy):
                class_value = int(classes[index])
                prompt_index = class_value if 0 <= class_value < len(prompts) else 0
                prompt = prompts[prompt_index]
                left, top, right, bottom = [float(value) for value in box]
                image_width, image_height = Image.open(target).size
                x = max(0.0, min(1.0, left / image_width))
                y = max(0.0, min(1.0, top / image_height))
                width = max(0.0, min(1.0, (right - left) / image_width))
                height = max(0.0, min(1.0, (bottom - top) / image_height))
                polygon = polygon_from_mask(mask_values[index]) if index < len(mask_values) else []
                item = {
                    "className": prompt["className"],
                    "shape": "polygon" if len(polygon) >= 3 else "bbox",
                    "x": x,
                    "y": y,
                    "width": width,
                    "height": height,
                    "polygon": polygon,
                    "score": float(scores[index]),
                }
                objects.append(item)
        results.append({"imagePath": str(target), "objects": objects})
    return {"results": results}


if __name__ == "__main__":
    try:
        request = json.loads(sys.stdin.read())
        print(json.dumps(main(request), ensure_ascii=False))
    except SystemExit:
        raise
    except Exception as error:
        fail(f"YOLOE 智能标注失败：{type(error).__name__}: {error}")
