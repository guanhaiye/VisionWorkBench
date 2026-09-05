"""One-shot Python inference worker for ATU5 semantic segmentation."""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path


def _device(value: str):
    import torch
    if value == "auto":
        return torch.device("cuda:0" if torch.cuda.is_available() else "cpu")
    return torch.device(value if value != "0" else "cuda:0")


def _class_names(request: dict, metadata: dict, count: int) -> list[str]:
    value = metadata.get("classes") or request.get("classes") or request.get("classNames") or []
    if isinstance(value, str):
        value = [x.strip() for x in value.replace("，", ",").split(",") if x.strip()]
    names = [str(x) for x in value]
    # Training manifests list foreground classes only. Accept that same form
    # in the test UI and add the model's background class at index 0.
    if len(names) == count - 1:
        names.insert(0, "background")
    if len(names) < count:
        names.extend(["background" if i == 0 else f"class{i}" for i in range(len(names), count)])
    return names[:count]


def run(request: dict, loaded_model=None, engine_runner=None) -> dict:
    import cv2
    import numpy as np
    import torch
    from model import load_model, read_image_size, read_model_metadata

    model_path = Path(str(request.get("modelPath", ""))).resolve()
    image_path = Path(str(request.get("imagePath", ""))).resolve()
    if not model_path.is_file():
        raise FileNotFoundError(f"模型不存在：{model_path}")
    if not image_path.is_file():
        raise FileNotFoundError(f"图片不存在：{image_path}")

    # cv2.imread uses the Windows ANSI file API and fails for the Chinese
    # workspace path. Read bytes first, then decode from memory instead.
    try:
        encoded = np.fromfile(str(image_path), dtype=np.uint8)
        image = cv2.imdecode(encoded, cv2.IMREAD_COLOR) if encoded.size else None
    except OSError:
        image = None
    if image is None:
        raise ValueError(f"无法读取图片：{image_path}")
    height, width = image.shape[:2]
    image_size = read_image_size(model_path)
    rgb = cv2.cvtColor(cv2.resize(image, (image_size, image_size)), cv2.COLOR_BGR2RGB)
    data = torch.from_numpy(rgb).permute(2, 0, 1).float().div(255.0)
    mean = torch.tensor([0.485, 0.456, 0.406]).view(3, 1, 1)
    std = torch.tensor([0.229, 0.224, 0.225]).view(3, 1, 1)
    data = ((data - mean) / std).unsqueeze(0)
    device = _device(str(request.get("device", "auto")))
    started = time.perf_counter()
    if loaded_model is None and engine_runner is None:
        loaded_model, engine_runner = prepare_model(model_path, device, image_size)
    with torch.inference_mode():
        if model_path.suffix.lower() == ".engine":
            logits = engine_runner.run(data, device)
        else:
            logits = loaded_model(data.to(device))
        prediction = logits.argmax(1)[0].to("cpu", torch.uint8).numpy()
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    prediction = cv2.resize(prediction, (width, height), interpolation=cv2.INTER_NEAREST)
    names = _class_names(request, read_model_metadata(model_path), int(logits.shape[1]))
    masks = []
    for class_id in range(1, int(logits.shape[1])):
        binary = (prediction == class_id).astype(np.uint8)
        count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, 8)
        for component in range(1, count):
            x, y, w, h, area = [int(v) for v in stats[component]]
            if area < int(request.get("minArea", 1)):
                continue
            contours, _ = cv2.findContours((labels == component).astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            if not contours:
                continue
            contour = max(contours, key=cv2.contourArea).reshape(-1, 2)
            if len(contour) < 3:
                continue
            if len(contour) > 300:
                contour = contour[::max(1, len(contour) // 300)]
            polygon = [{"x": float(px) / width, "y": float(py) / height} for px, py in contour]
            item = {"classId": class_id, "className": names[class_id], "confidence": 1.0,
                    "x": x / width, "y": y / height, "width": w / width, "height": h / height,
                    "areaRatio": float(area) / max(1, width * height)}
            masks.append({**item, "polygon": polygon})
    # Semantic segmentation returns regions only. Bounding boxes are an
    # instance/detection concept and must not leak into the semantic platform.
    return {"success": True, "task": "semantic", "elapsedMs": elapsed_ms,
            "imageWidth": width, "imageHeight": height,
            "classNames": names, "masks": masks}


def prepare_model(model_path: Path, device, image_size: int):
    import torch
    from model import load_model, read_model_metadata

    if model_path.suffix.lower() == ".engine":
        return None, _EngineRunner(model_path)
    metadata = read_model_metadata(model_path)
    num_classes = int(metadata.get("numClasses", metadata.get("num_classes", 2)))
    return load_model(model_path, device, num_classes, image_size), None


def _run_engine(engine_path: Path, data, device):
    return _EngineRunner(engine_path).run(data, device)


class _EngineRunner:
    def __init__(self, engine_path: Path):
        import tensorrt as trt

        self._trt = trt
        logger = trt.Logger(trt.Logger.ERROR)
        with engine_path.open("rb") as stream:
            runtime = trt.Runtime(logger)
            self._engine = runtime.deserialize_cuda_engine(stream.read())
        if self._engine is None:
            raise RuntimeError("无法加载 ATU5 TensorRT Engine")
        self._runtime = runtime
        self._context = self._engine.create_execution_context()
        tensor_names = [self._engine.get_tensor_name(index) for index in range(self._engine.num_io_tensors)]
        self._input_name = next(name for name in tensor_names if self._engine.get_tensor_mode(name) == trt.TensorIOMode.INPUT)
        self._output_name = next(name for name in tensor_names if self._engine.get_tensor_mode(name) == trt.TensorIOMode.OUTPUT)

    def run(self, data, device):
        import numpy as np
        import torch

        if device.type != "cuda":
            raise RuntimeError("ATU5 TensorRT Engine 推理需要 CUDA 设备")
        input_tensor = data.to(device, non_blocking=True).contiguous()
        self._context.set_input_shape(self._input_name, tuple(input_tensor.shape))
        output_shape = tuple(self._context.get_tensor_shape(self._output_name))
        if any(int(value) < 0 for value in output_shape):
            raise RuntimeError(f"ATU5 Engine 输出尺寸无效：{output_shape}")
        output_numpy_dtype = self._trt.nptype(self._engine.get_tensor_dtype(self._output_name))
        output_dtype = torch.from_numpy(np.empty((), dtype=output_numpy_dtype)).dtype
        output_tensor = torch.empty(output_shape, dtype=output_dtype, device=device)
        self._context.set_tensor_address(self._input_name, int(input_tensor.data_ptr()))
        self._context.set_tensor_address(self._output_name, int(output_tensor.data_ptr()))
        if not self._context.execute_async_v3(torch.cuda.current_stream(device).cuda_stream):
            raise RuntimeError("ATU5 TensorRT Engine 执行失败")
        torch.cuda.synchronize(device)
        return output_tensor


def _legacy_run_engine(engine_path: Path, data, device):
    import numpy as np
    import tensorrt as trt
    import torch

    if device.type != "cuda":
        raise RuntimeError("ATU5 TensorRT Engine 推理需要 CUDA 设备")
    logger = trt.Logger(trt.Logger.ERROR)
    with engine_path.open("rb") as stream:
        runtime = trt.Runtime(logger)
        engine = runtime.deserialize_cuda_engine(stream.read())
    if engine is None:
        raise RuntimeError("无法加载 ATU5 TensorRT Engine")
    context = engine.create_execution_context()
    tensor_names = [engine.get_tensor_name(index) for index in range(engine.num_io_tensors)]
    input_name = next(name for name in tensor_names if engine.get_tensor_mode(name) == trt.TensorIOMode.INPUT)
    output_name = next(name for name in tensor_names if engine.get_tensor_mode(name) == trt.TensorIOMode.OUTPUT)
    input_tensor = data.to(device, non_blocking=True).contiguous()
    context.set_input_shape(input_name, tuple(input_tensor.shape))
    output_shape = tuple(context.get_tensor_shape(output_name))
    if any(int(value) < 0 for value in output_shape):
        raise RuntimeError(f"ATU5 Engine 输出尺寸无效：{output_shape}")
    output_numpy_dtype = trt.nptype(engine.get_tensor_dtype(output_name))
    output_dtype = torch.from_numpy(np.empty((), dtype=output_numpy_dtype)).dtype
    output_tensor = torch.empty(output_shape, dtype=output_dtype, device=device)
    context.set_tensor_address(input_name, int(input_tensor.data_ptr()))
    context.set_tensor_address(output_name, int(output_tensor.data_ptr()))
    if not context.execute_async_v3(torch.cuda.current_stream(device).cuda_stream):
        raise RuntimeError("ATU5 TensorRT Engine 执行失败")
    torch.cuda.synchronize(device)
    return output_tensor


def main() -> int:
    try:
        payload = sys.stdin.read().lstrip("\ufeff").strip()
        if not payload:
            raise ValueError("语义分割测试进程没有收到请求数据")
        print(json.dumps(run(json.loads(payload)), ensure_ascii=False))
        return 0
    except Exception as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
