"""Export a trained ATU5/FPN checkpoint to ONNX and TensorRT Engine."""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

import torch

from model import load_model


def export_engine(model_path: str | Path, image_size: int | None = None) -> str:
    model_path = Path(model_path).resolve()
    if not model_path.is_file() or model_path.suffix.lower() != ".pt":
        raise FileNotFoundError(f"ATU5 模型不存在：{model_path}")

    checkpoint = torch.load(model_path, map_location="cpu", weights_only=False)
    metadata = checkpoint.get("meta", {}) if isinstance(checkpoint, dict) else {}
    size = int(image_size or metadata.get("imageSize", 512))
    num_classes = int(metadata.get("numClasses", 2))
    model = load_model(model_path, torch.device("cpu"), num_classes, size)
    model.eval()

    import onnx  # noqa: F401
    import tensorrt as trt

    with tempfile.TemporaryDirectory(prefix="visionworkbench_atu5_") as temp_dir:
        onnx_path = Path(temp_dir) / "model.onnx"
        dummy = torch.randn(1, 3, size, size, dtype=torch.float32)
        torch.onnx.export(
            model,
            dummy,
            str(onnx_path),
            input_names=["images"],
            output_names=["logits"],
            opset_version=17,
            dynamo=False,
        )

        logger = trt.Logger(trt.Logger.WARNING)
        builder = trt.Builder(logger)
        explicit_batch = getattr(trt.NetworkDefinitionCreationFlag, "EXPLICIT_BATCH", None)
        network_flags = 1 << int(explicit_batch) if explicit_batch is not None else 0
        network = builder.create_network(network_flags)
        parser = trt.OnnxParser(network, logger)
        if not parser.parse_from_file(str(onnx_path)):
            errors = "; ".join(str(parser.get_error(index)) for index in range(parser.num_errors))
            raise RuntimeError(f"TensorRT 解析 ATU5 ONNX 失败：{errors}")

        config = builder.create_builder_config()
        config.set_memory_pool_limit(trt.MemoryPoolType.WORKSPACE, 2 << 30)
        if getattr(builder, "platform_has_fast_fp16", False):
            config.set_flag(trt.BuilderFlag.FP16)
        serialized = builder.build_serialized_network(network, config)
        if serialized is None:
            raise RuntimeError("TensorRT 构建 ATU5 Engine 失败")

    engine_path = model_path.with_suffix(".engine")
    engine_path.write_bytes(bytes(serialized))
    engine_path.with_suffix(".engine.meta.json").write_text(
        json.dumps(
            {
                "task": "semantic",
                "imageSize": size,
                "numClasses": num_classes,
                "classes": metadata.get("classes", []),
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    return str(engine_path)


def main() -> int:
    try:
        payload = sys.stdin.read().lstrip("\ufeff").strip()
        if not payload:
            raise ValueError("导出进程没有收到请求数据")
        request = json.loads(payload)
        engine_path = export_engine(request.get("modelPath", ""), request.get("imageSize"))
        print(json.dumps({"success": True, "enginePath": engine_path}, ensure_ascii=False))
        return 0
    except Exception as error:  # noqa: BLE001
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
