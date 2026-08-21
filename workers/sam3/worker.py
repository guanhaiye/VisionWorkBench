"""VisionWorkbench SAM3 click-to-segmentation adapter.

The public SAM3 image processor currently exposes normalized geometric boxes.
For a click-only desktop workflow this adapter converts a positive click into
a small normalized positive box, then stores the returned mask as a polygon.
The conversion is kept here so the UI remains a true click workflow and the
adapter can switch to a native point API when one becomes available.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(2)


def mask_polygon(mask):
    import cv2
    import numpy as np

    array = mask.astype(np.uint8) * 255
    contours, _ = cv2.findContours(array, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return []
    contour = max(contours, key=cv2.contourArea)
    epsilon = max(1.0, 0.002 * cv2.arcLength(contour, True))
    contour = cv2.approxPolyDP(contour, epsilon, True)
    height, width = array.shape[:2]
    return [
        {"x": float(point[0][0] / max(1, width - 1)), "y": float(point[0][1] / max(1, height - 1))}
        for point in contour
    ]


def main(payload: dict) -> dict:
    model_path = Path(payload.get("modelPath", ""))
    image_path = Path(payload.get("image", ""))
    if not image_path.is_file():
        fail(f"图片不存在：{image_path}")
    points = payload.get("points", [])
    if not points:
        fail("SAM3 至少需要一个点击点")

    try:
        import numpy as np
        from sam3.model_builder import build_sam3_image_model
        from sam3.model.sam3_image_processor import Sam3Processor
    except ImportError as error:
        fail(f"SAM3 运行环境不完整，请按官方仓库安装 sam3：{error}")

    try:
        from PIL import Image

        image = Image.open(image_path).convert("RGB")
        # SAM3 官方 checkpoint 需要 Hugging Face 授权。没有本地文件时，
        # 让官方 builder 使用其默认缓存/认证下载流程，而不是要求用户选择路径。
        model = (
            build_sam3_image_model(checkpoint_path=str(model_path))
            if model_path.is_file()
            else build_sam3_image_model()
        )
        processor = Sam3Processor(model)
        state = processor.set_image(image)
        for point in points:
            x = float(max(0.0, min(1.0, point["x"])))
            y = float(max(0.0, min(1.0, point["y"])))
            # Public SAM3 image processor accepts [cx, cy, width, height].
            box_width = 0.02
            box_height = 0.02
            state = processor.add_geometric_prompt(
                [x, y, box_width, box_height],
                bool(point.get("label", 1)),
                state,
            ) or state
        masks = state.get("masks")
        scores = state.get("scores")
        if masks is None or len(masks) == 0:
            return {"results": []}
        mask_values = masks.detach().cpu().numpy() if hasattr(masks, "detach") else np.asarray(masks)
        if mask_values.ndim == 4:
            mask_values = mask_values[:, 0]
        best_index = 0
        if scores is not None and len(scores) > 0:
            score_values = scores.detach().cpu().numpy() if hasattr(scores, "detach") else np.asarray(scores)
            best_index = int(np.argmax(score_values))
            score = float(score_values[best_index])
        else:
            score = 0.0
        polygon = mask_polygon(mask_values[best_index] > 0)
        if len(polygon) < 3:
            return {"results": []}
        xs = [item["x"] for item in polygon]
        ys = [item["y"] for item in polygon]
        return {
            "results": [{
                "imagePath": str(image_path),
                "objects": [{
                    "className": payload.get("className", "object"),
                    "shape": "polygon",
                    "x": min(xs),
                    "y": min(ys),
                    "width": max(xs) - min(xs),
                    "height": max(ys) - min(ys),
                    "polygon": polygon,
                    "score": score,
                }],
            }],
        }
    except Exception as error:
        fail(f"SAM3 点击分割失败：{type(error).__name__}: {error}")


if __name__ == "__main__":
    try:
        print(json.dumps(main(json.loads(sys.stdin.read())), ensure_ascii=False))
    except SystemExit:
        raise
    except Exception as error:
        fail(f"SAM3 智能标注失败：{type(error).__name__}: {error}")
