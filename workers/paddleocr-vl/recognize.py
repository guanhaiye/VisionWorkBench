"""PaddleOCR-VL-1.6 one-shot worker used by the desktop character test platform."""
from __future__ import annotations

import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: recognize.py <kind> <image> <output-dir>", file=sys.stderr)
        return 2
    image = Path(sys.argv[2]).resolve()
    output = Path(sys.argv[3]).resolve()
    if not image.is_file():
        print(f"图片不存在：{image}", file=sys.stderr)
        return 2
    output.mkdir(parents=True, exist_ok=True)
    try:
        from paddleocr import PaddleOCRVL

        pipeline = PaddleOCRVL(pipeline_version="v1.6")
        for result in pipeline.predict(str(image)):
            result.save_to_json(save_path=output)
            result.save_to_markdown(save_path=output)
        print("PaddleOCR-VL-1.6 识别完成")
        return 0
    except Exception as error:  # noqa: BLE001 - worker must return a readable desktop error
        print(f"PaddleOCR-VL-1.6 识别失败：{error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
