"""Persistent SAM1 click-annotation worker using line-delimited JSON."""
from __future__ import annotations
import argparse, json, sys
from pathlib import Path

_predictor = None
_model_path = None
_image_path = None

def load_predictor(model_path: Path):
    global _predictor, _model_path
    if not model_path.is_file(): raise FileNotFoundError(f"SAM1 model not found: {model_path}")
    resolved = str(model_path.resolve())
    if _predictor is None or _model_path != resolved:
        import torch
        from segment_anything import SamPredictor, sam_model_registry
        model = sam_model_registry["vit_b"](checkpoint=resolved)
        model.to(device="cuda" if torch.cuda.is_available() else "cpu").eval()
        _predictor, _model_path = SamPredictor(model), resolved
    return _predictor

def polygon_from_mask(mask):
    import cv2, numpy as np
    binary = mask.astype(np.uint8) * 255
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours: return []
    contour = max(contours, key=cv2.contourArea)
    contour = cv2.approxPolyDP(contour, max(1.0, .0015 * cv2.arcLength(contour, True)), True)
    height, width = binary.shape[:2]
    return [{"x": float(p[0][0] / max(1, width-1)), "y": float(p[0][1] / max(1, height-1))} for p in contour]

def handle(payload: dict) -> dict:
    global _image_path
    predictor = load_predictor(Path(payload.get("modelPath", "")))
    if payload.get("type") == "warmup": return {"ready": True, "results": []}
    import cv2, numpy as np
    image_path = Path(payload.get("image", ""))
    if not image_path.is_file(): raise FileNotFoundError(f"Image not found: {image_path}")
    resolved = str(image_path.resolve())
    if _image_path != resolved:
        # cv2.imread cannot reliably open non-ASCII Windows paths.
        image = cv2.imdecode(np.fromfile(resolved, dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None: raise ValueError(f"Unable to read image: {image_path}")
        predictor.set_image(cv2.cvtColor(image, cv2.COLOR_BGR2RGB)); _image_path = resolved
    points = payload.get("points") or []
    if not points: raise ValueError("At least one click point is required")
    height, width = predictor.original_size
    coords = np.array([[float(p["x"])*width, float(p["y"])*height] for p in points])
    labels = np.array([int(p.get("label", 1)) for p in points])
    masks, scores, _ = predictor.predict(point_coords=coords, point_labels=labels, multimask_output=True)
    best = int(np.argmax(scores)); polygon = polygon_from_mask(masks[best])
    if len(polygon) < 3: return {"ready": True, "results": [{"imagePath": resolved, "objects": []}]}
    xs, ys = [p["x"] for p in polygon], [p["y"] for p in polygon]
    obj = {"className": payload.get("className") or "object", "shape": "polygon",
           "x": min(xs), "y": min(ys), "width": max(xs)-min(xs), "height": max(ys)-min(ys),
           "polygon": polygon, "score": float(scores[best])}
    return {"ready": True, "results": [{"imagePath": resolved, "objects": [obj]}]}

def process(line: str):
    try: result = handle(json.loads(line))
    except Exception as error: result = {"ready": False, "results": [], "error": str(error)}
    print(json.dumps(result, ensure_ascii=False), flush=True)

def main():
    parser = argparse.ArgumentParser(); parser.add_argument("--persistent", action="store_true"); args = parser.parse_args()
    if args.persistent:
        for line in sys.stdin:
            if line.strip(): process(line)
    else: process(sys.stdin.read())

if __name__ == "__main__": main()
