"""VisionWorkbench YOLO11 训练 Worker。

通过标准输入接收一条 JSON 训练请求，并在标准输出中输出 JSON 事件。
Ultralytics 自身的文本日志会同时输出，但宿主只消费带 event 字段的 JSON 行。
"""

from __future__ import annotations

import json
import sys
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
    if task not in {"detection", "instance_segmentation"}:
        raise ValueError(f"不支持的数据集类型: {task}")

    data_yaml = Path(str(request["dataYaml"])).resolve()
    model_path = str(request["modelPath"])
    model = YOLO(model_path)
    expected_model_task = "detect" if task == "detection" else "segment"
    actual_model_task = str(getattr(model, "task", "")).lower()
    if actual_model_task and actual_model_task != expected_model_task:
        expected_name = "目标检测" if task == "detection" else "实例分割"
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
            values = [scalar(metrics.get(key)) for key in ("val/box_loss", "val/seg_loss", "val/cls_loss", "val/dfl_loss")]
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
    emit(
        "completed",
        modelPath=str(model_path_result),
        runDirectory=str(save_dir),
        message=f"训练完成，模型已保存到 {model_path_result}",
    )


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
