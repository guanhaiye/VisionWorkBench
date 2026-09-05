"""Batch ATU5 semantic segmentation tester with one model load per batch."""
from __future__ import annotations

import json
import sys
from pathlib import Path


def emit(values: dict) -> None:
    print(json.dumps(values, ensure_ascii=False), flush=True)


def main() -> int:
    try:
        request = json.loads(sys.stdin.read().lstrip("\ufeff").strip())
        model_path = Path(str(request.get("modelPath", ""))).resolve()
        image_paths = request.get("imagePaths", [])
        if not model_path.is_file():
            raise FileNotFoundError(f"模型不存在：{model_path}")
        if not isinstance(image_paths, list) or not image_paths:
            raise ValueError("语义分割测试图片列表为空")

        import torch
        from model import read_image_size
        from test_worker import prepare_model, run

        device = _device(str(request.get("device", "auto")))
        image_size = read_image_size(model_path)
        loaded_model, engine_runner = prepare_model(model_path, device, image_size)
        total = len(image_paths)
        for index, image_value in enumerate(image_paths, start=1):
            image_path = Path(str(image_value)).resolve()
            try:
                result = run(
                    {"modelPath": str(model_path), "imagePath": str(image_path), "device": str(request.get("device", "auto"))},
                    loaded_model=loaded_model,
                    engine_runner=engine_runner,
                )
                emit({"event": "result", "imagePath": str(image_path), **result})
            except Exception as error:  # noqa: BLE001
                emit({"event": "result", "imagePath": str(image_path), "success": False, "error": str(error)})
            emit({"event": "progress", "completed": index, "total": total})
        emit({"event": "completed", "success": True})
        return 0
    except Exception as error:  # noqa: BLE001
        emit({"event": "error", "success": False, "error": str(error)})
        return 1


def _device(value: str):
    import torch

    if value == "auto":
        return torch.device("cuda:0" if torch.cuda.is_available() else "cpu")
    return torch.device(value if value != "0" else "cuda:0")


if __name__ == "__main__":
    raise SystemExit(main())
