"""One-shot PyTorch to TensorRT Engine exporter."""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
from pathlib import Path


def main() -> int:
    try:
        payload = sys.stdin.read().lstrip("\ufeff").strip()
        if not payload:
            raise ValueError("导出进程没有收到请求数据")
        request = json.loads(payload)
        model_path = Path(str(request.get("modelPath", ""))).resolve()
        if not model_path.is_file() or model_path.suffix.lower() != ".pt":
            raise FileNotFoundError(f"PyTorch 模型不存在：{model_path}")
        from ultralytics import YOLO

        model = YOLO(str(model_path))
        task = str(getattr(model, "task", "detect")).lower()
        image_size = read_model_image_size(model_path, model)
        target_engine = model_path.with_suffix(".engine")
        # TensorRT's Windows ONNX parser cannot open paths containing CJK text.
        # Export in an ASCII-only temporary directory, then copy the final engine back.
        with tempfile.TemporaryDirectory(prefix="visionworkbench_trt_") as temp_directory:
            temporary_model = Path(temp_directory) / model_path.name
            shutil.copy2(model_path, temporary_model)
            exported = YOLO(str(temporary_model)).export(format="engine", imgsz=image_size, device="0")
            temporary_engine = Path(str(exported)).resolve()
            if not temporary_engine.is_file():
                raise FileNotFoundError(f"导出完成但没有找到 Engine 文件：{temporary_engine}")
            shutil.copy2(temporary_engine, target_engine)
        target_engine.with_suffix(".engine.task").write_text(task, encoding="utf-8")
        target_engine.with_suffix(".engine.meta.json").write_text(
            json.dumps({"imageSize": image_size, "task": task}, ensure_ascii=False, indent=2),
            encoding="utf-8")
        print(json.dumps({"success": True, "enginePath": str(target_engine)}, ensure_ascii=False))
        return 0
    except Exception as error:  # noqa: BLE001
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False))
        return 1


def read_model_image_size(model_path: Path, model, default: int = 640) -> int:
    import torch  # type: ignore

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


if __name__ == "__main__":
    raise SystemExit(main())
