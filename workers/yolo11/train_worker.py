"""VisionWorkbench YOLO11 训练 Worker。

通过标准输入接收一条 JSON 训练请求，并在标准输出中输出 JSON 事件。
Ultralytics 自身的文本日志会同时输出，但宿主只消费带 event 字段的 JSON 行。
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
import time
import traceback
from pathlib import Path
from typing import Any


def emit(event: str, **values: Any) -> None:
    payload = {"event": event, **values}
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def scalar(value: Any) -> float | None:
    if value is None:
        return None
    try:
        if isinstance(value, dict):
            values = [scalar(item) for item in value.values()]
            values = [item for item in values if item is not None]
            return sum(values) if values else None
        if hasattr(value, "detach"):
            value = value.detach()
        if hasattr(value, "cpu"):
            value = value.cpu()
        if hasattr(value, "tolist"):
            value = value.tolist()
        if isinstance(value, (list, tuple)):
            values = [scalar(item) for item in value]
            values = [item for item in values if item is not None]
            return sum(values) if values else None
        return float(value)
    except (TypeError, ValueError):
        return None


def train(request: dict[str, Any]) -> None:
    from ultralytics import YOLO  # type: ignore

    task = str(request.get("taskType", "detection"))
    if task == "semantic_segmentation":
        raise ValueError("YOLO11 官方不支持原生语义分割训练，请使用实例分割或其他语义分割模型。")
    if task not in {"detection", "instance_segmentation", "pose"}:
        raise ValueError(f"不支持的数据集类型: {task}")

    data_yaml = Path(str(request["dataYaml"])).resolve()
    model_path = str(request["modelPath"])
    model = YOLO(model_path)
    expected_model_task = {"detection": "detect", "instance_segmentation": "segment", "pose": "pose"}[task]
    actual_model_task = str(getattr(model, "task", "")).lower()
    if actual_model_task and actual_model_task != expected_model_task:
        expected_name = {"detection": "目标检测", "instance_segmentation": "实例分割", "pose": "关键点检测"}[task]
        raise ValueError(f"当前模型任务为 {actual_model_task}，与数据集类型 {expected_name} 不匹配。")

    epochs = int(request.get("epochs", 100))
    batch = int(request.get("batchSize", 8))
    imgsz = int(request.get("imageSize", 640))
    device = str(request.get("device", "auto"))
    if device == "auto":
        try:
            import torch  # type: ignore

            device = "0" if torch.cuda.is_available() else "cpu"
        except ImportError:
            device = "cpu"

    output_directory = Path(str(request["outputDirectory"])).resolve()
    run_name = str(request["runName"])
    output_directory.mkdir(parents=True, exist_ok=True)
    pause_file = str(request.get("pauseFilePath") or "").strip()

    def wait_if_paused() -> None:
        while pause_file and Path(pause_file).is_file():
            time.sleep(0.2)

    total_epochs = epochs

    def on_fit_epoch_end(trainer: Any) -> None:
        epoch = int(getattr(trainer, "epoch", 0)) + 1
        loss_items = getattr(trainer, "tloss", None)
        if loss_items is None:
            loss_items = getattr(trainer, "loss_items", None)
        train_loss = scalar(loss_items)
        metrics = getattr(trainer, "metrics", {}) or {}
        val_loss = None
        if isinstance(metrics, dict):
            values = [scalar(metrics.get(key)) for key in ("val/box_loss", "val/seg_loss", "val/pose_loss", "val/kobj_loss", "val/cls_loss", "val/dfl_loss")]
            values = [value for value in values if value is not None]
            val_loss = sum(values) if values else None
        emit(
            "epoch",
            epoch=epoch,
            totalEpochs=total_epochs,
            trainLoss=train_loss,
            valLoss=val_loss,
            message=f"第 {epoch}/{total_epochs} 轮完成",
        )

    def on_train_epoch_start(trainer: Any) -> None:
        wait_if_paused()

    model.add_callback("on_train_epoch_start", on_train_epoch_start)
    model.add_callback("on_fit_epoch_end", on_fit_epoch_end)
    emit(
        "started",
        totalEpochs=total_epochs,
        message="YOLO11 训练已启动" + ("，正在继续已有模型" if request.get("resume") else ""),
    )
    results = model.train(
        data=str(data_yaml),
        epochs=epochs,
        batch=batch,
        imgsz=imgsz,
        device=device,
        project=str(output_directory),
        name=run_name,
        exist_ok=True,
        resume=bool(request.get("resume", False)),
        save=True,
        plots=True,
        verbose=True,
    )
    save_dir = Path(str(getattr(results, "save_dir", output_directory / run_name))).resolve()
    best_path = save_dir / "weights" / "best.pt"
    last_path = save_dir / "weights" / "last.pt"
    model_path_result = best_path if best_path.is_file() else last_path
    if not model_path_result.is_file():
        raise FileNotFoundError(f"训练完成但没有找到模型文件: {model_path_result}")
    for saved_model in (best_path, last_path):
        if saved_model.is_file():
            stamp_model_metadata(saved_model, imgsz, expected_model_task, getattr(model, "names", {}))
    engine_path = None
    if bool(request.get("exportTensorRt", True)):
        emit("log", message="训练完成，正在导出 TensorRT Engine…")
        try:
            target_engine = model_path_result.with_suffix(".engine")
            with tempfile.TemporaryDirectory(prefix="visionworkbench_trt_") as temp_directory:
                temporary_model = Path(temp_directory) / model_path_result.name
                shutil.copy2(model_path_result, temporary_model)
                exported = YOLO(str(temporary_model)).export(format="engine", imgsz=imgsz, device="0")
                temporary_engine = Path(str(exported)).resolve()
                shutil.copy2(temporary_engine, target_engine)
            engine_path = str(target_engine)
            target_engine.with_suffix(".engine.task").write_text(expected_model_task, encoding="utf-8")
            target_engine.with_suffix(".engine.meta.json").write_text(
                json.dumps({"imageSize": imgsz, "task": expected_model_task,
                            "classes": _class_names(getattr(model, "names", {}))},
                           ensure_ascii=False, indent=2),
                encoding="utf-8")
            emit("log", message=f"TensorRT Engine 已导出：{engine_path}")
        except Exception as export_error:  # noqa: BLE001
            emit("log", message=f"模型训练成功，但 TensorRT Engine 导出失败：{export_error}")
    emit(
        "completed",
        modelPath=str(model_path_result),
        runDirectory=str(save_dir),
        enginePath=engine_path,
        message=f"训练完成，模型已保存到 {model_path_result}" + (f"，Engine 已保存到 {engine_path}" if engine_path else ""),
    )


def _class_names(names: Any) -> list[str]:
    if isinstance(names, dict):
        ordered = sorted(names.items(), key=lambda pair: (0, int(pair[0]))
                         if str(pair[0]).isdigit() else (1, str(pair[0])))
        return [str(value) for _, value in ordered]
    if isinstance(names, (list, tuple)):
        return [str(value) for value in names]
    return []


def stamp_model_metadata(model_path: Path, image_size: int, task: str, names: Any) -> None:
    """Store VisionWorkbench inference metadata inside each YOLO checkpoint."""
    import torch  # type: ignore

    checkpoint = torch.load(model_path, map_location="cpu", weights_only=False)
    if not isinstance(checkpoint, dict):
        raise ValueError(f"YOLO model format does not support metadata: {model_path}")
    metadata = dict(checkpoint.get("visionWorkbench") or {})
    classes = _class_names(names)
    metadata.update({"imageSize": int(image_size), "task": task, "classes": classes})
    checkpoint["visionWorkbench"] = metadata
    torch.save(checkpoint, model_path)
    model_path.with_suffix(".json").write_text(
        json.dumps({"imageSize": int(image_size), "task": task, "classes": classes},
                   ensure_ascii=False, indent=2),
        encoding="utf-8")


def main() -> int:
    try:
        line = sys.stdin.readline()
        if not line.strip():
            raise ValueError("训练请求为空")
        train(json.loads(line))
        return 0
    except Exception as error:  # noqa: BLE001
        emit("error", message=str(error))
        traceback.print_exc(file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
