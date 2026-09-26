"""One-shot barcode/QR decoder used by the AI code recognition platforms."""
from __future__ import annotations

import json
import sys
from pathlib import Path


def decode_qr(image):
    import cv2

    detector = cv2.QRCodeDetector()
    results = []
    try:
        ok, values, points, _ = detector.detectAndDecodeMulti(image)
        if ok and values:
            results.extend(value for value in values if value)
    except (AttributeError, cv2.error):
        pass
    if not results:
        value, _, _ = detector.detectAndDecode(image)
        if value:
            results.append(value)
    return results


def decode_barcode(image):
    import cv2

    if not hasattr(cv2, "barcode"):
        raise RuntimeError("当前 OpenCV 未包含 BarcodeDetector，请安装 opencv-contrib-python-headless。")
    detector = cv2.barcode.BarcodeDetector()
    if hasattr(detector, "detectAndDecodeWithType"):
        _, values, _, _ = detector.detectAndDecodeWithType(image)
    else:
        result = detector.detectAndDecode(image)
        # Older barcode-specific APIs return (success, values, types, points).
        # Newer inherited GraphicalCodeDetector APIs return (text, points, image).
        values = result[1] if len(result) == 4 else result[0]
    if isinstance(values, str):
        values = [values] if values else []
    return [value for value in values if value]


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: decode_code.py <barcode|qrcode> <image> <output-dir>", file=sys.stderr)
        return 2
    kind = sys.argv[1]
    image_path = Path(sys.argv[2]).resolve()
    if not image_path.is_file():
        print(f"图片不存在：{image_path}", file=sys.stderr)
        return 2
    try:
        import cv2
        import numpy as np

        image = cv2.imdecode(np.frombuffer(image_path.read_bytes(), dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None:
            raise RuntimeError("图片无法读取。")
        values = decode_barcode(image) if kind == "barcode" else decode_qr(image)
        print(json.dumps({"success": True, "kind": kind, "values": values}, ensure_ascii=False))
        return 0
    except Exception as error:  # noqa: BLE001 - worker must return a readable desktop error
        print(f"码制解析失败：{error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
