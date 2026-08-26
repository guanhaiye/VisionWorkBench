"""Train a small custom temporal behavior classifier from annotated image clips.

The worker first extracts YOLO11-Pose keypoints and then trains a TorchScript
classifier.  Its input layout is [N, C, T, V], matching temporal_pose.py.
All stdout records that matter to the desktop app are JSON events.
"""

from __future__ import annotations

import json
import math
import random
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any

import numpy as np
import torch
from torch import nn


def emit(event: str, message: str = "", **payload: Any) -> None:
    data = {"event": event, "message": message, **payload}
    print(json.dumps(data, ensure_ascii=False), flush=True)


class BehaviorNet(nn.Module):
    def __init__(self, class_count: int) -> None:
        super().__init__()
        self.features = nn.Sequential(
            nn.Conv1d(51, 128, kernel_size=3, padding=1),
            nn.ReLU(),
            nn.Conv1d(128, 64, kernel_size=3, padding=1),
            nn.ReLU(),
            nn.AdaptiveAvgPool1d(1),
        )
        self.classifier = nn.Linear(64, class_count)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x: [N, C, T, V] -> [N, C*V, T]
        x = x.permute(0, 1, 3, 2).reshape(x.shape[0], 51, x.shape[2])
        return self.classifier(self.features(x).flatten(1))


def load_dataset(path: Path) -> tuple[list[str], list[tuple[str, list[str], str]], Path, int]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    root = Path(data.get("rootDirectory") or path.parent.parent).resolve()
    classes = [str(item).strip() for item in data.get("classes", []) if str(item).strip()]
    clips: list[tuple[str, list[str], str]] = []
    for clip in data.get("clips", []):
        label = str(clip.get("label", "")).strip()
        source_id = str(clip.get("sourceId", "")).strip() or f"legacy-{len(clips)}"
        frames = [str(frame) for frame in clip.get("frames", [])]
        valid = [str((root / frame).resolve()) for frame in frames if (root / frame).is_file()]
        if label and len(valid) >= 2:
            clips.append((label, valid, source_id))
    sequence_length = max(8, int(data.get("sequenceLength", 30)))
    return classes, clips, root, sequence_length


def device_name(value: str) -> str:
    if value == "cpu":
        return "cpu"
    if torch.cuda.is_available():
        return "cuda:0"
    if value not in {"auto", "0"}:
        return value
    return "cpu"


def pose_for_frame(model: Any, image_path: str, device: str) -> np.ndarray:
    try:
        results = model.predict(source=image_path, device=device, verbose=False)
        if not results:
            return np.zeros((17, 3), dtype=np.float32)
        keypoints = getattr(results[0], "keypoints", None)
        xy = getattr(keypoints, "xy", None)
        conf = getattr(keypoints, "conf", None)
        if xy is None or len(xy) == 0:
            return np.zeros((17, 3), dtype=np.float32)
        xy_np = xy.cpu().numpy() if hasattr(xy, "cpu") else np.asarray(xy)
        conf_np = conf.cpu().numpy() if hasattr(conf, "cpu") else np.asarray(conf) if conf is not None else None
        person = 0
        points = np.zeros((17, 3), dtype=np.float32)
        image = results[0].orig_img
        height, width = image.shape[:2]
        for index in range(min(17, xy_np.shape[1])):
            points[index, 0] = float(np.clip(xy_np[person, index, 0] / max(1, width), 0, 1))
            points[index, 1] = float(np.clip(xy_np[person, index, 1] / max(1, height), 0, 1))
            points[index, 2] = float(conf_np[person, index]) if conf_np is not None else 1.0
        return points
    except Exception:
        return np.zeros((17, 3), dtype=np.float32)


def resample(frames: list[np.ndarray], length: int) -> np.ndarray:
    if not frames:
        return np.zeros((length, 17, 3), dtype=np.float32)
    if len(frames) == length:
        return np.asarray(frames, dtype=np.float32)
    if len(frames) > length:
        indexes = np.linspace(0, len(frames) - 1, length).round().astype(int)
        return np.asarray([frames[index] for index in indexes], dtype=np.float32)
    padding = [frames[-1]] * (length - len(frames))
    return np.asarray([*frames, *padding], dtype=np.float32)


def prepare_samples(classes: list[str], clips: list[tuple[str, list[str], str]], root: Path, length: int, pose_model: str, device: str) -> tuple[torch.Tensor, torch.Tensor]:
    from ultralytics import YOLO

    model = YOLO(pose_model)
    cache_path = root / ".visionworkbench" / "behavior-pose-cache.json"
    cache: dict[str, Any] = {}
    if cache_path.is_file():
        try:
            cache = json.loads(cache_path.read_text(encoding="utf-8"))
        except Exception:
            cache = {}
    samples: list[np.ndarray] = []
    targets: list[int] = []
    for clip_index, (label, frames, _) in enumerate(clips, start=1):
        extracted: list[np.ndarray] = []
        for frame in frames:
            key = str(Path(frame).resolve())
            cached = cache.get(key)
            if cached is not None:
                extracted.append(np.asarray(cached, dtype=np.float32).reshape(17, 3))
            else:
                pose = pose_for_frame(model, frame, device)
                cache[key] = pose.tolist()
                extracted.append(pose)
        samples.append(resample(extracted, length))
        targets.append(classes.index(label))
        emit("log", f"已提取第 {clip_index}/{len(clips)} 个片段的 Pose 特征")
    cache_path.parent.mkdir(parents=True, exist_ok=True)
    cache_path.write_text(json.dumps(cache, ensure_ascii=False), encoding="utf-8")
    # [N, T, V, C] -> [N, C, T, V]
    features = torch.tensor(np.asarray(samples), dtype=torch.float32).permute(0, 3, 1, 2)
    return features, torch.tensor(targets, dtype=torch.long)


def main(request: dict[str, Any]) -> None:
    dataset_path = Path(request["datasetPath"]).resolve()
    classes, clips, root, dataset_length = load_dataset(dataset_path)
    if len(classes) < 2:
        raise ValueError("行为训练至少需要两个类别。")
    if len(clips) < 2:
        raise ValueError("行为训练至少需要两个有效行为片段。")
    labels = {label for label, _, _ in clips}
    if len(labels) < 2:
        raise ValueError("行为片段至少要覆盖两个类别。")

    epochs = max(1, int(request.get("epochs", 50)))
    batch_size = max(1, int(request.get("batchSize", 2)))
    length = max(8, int(request.get("sequenceLength", dataset_length)))
    device = device_name(str(request.get("device", "auto")))
    emit("started", f"正在提取 Pose 特征（设备：{device}）", totalEpochs=epochs)
    features, targets = prepare_samples(classes, clips, root, length, str(request["poseModelPath"]), device)

    random.seed(42)
    # 按视频/序列来源划分，避免同一视频的相邻片段同时出现在训练和验证集造成泄漏。
    source_ids = sorted({source_id for _, _, source_id in clips})
    random.shuffle(source_ids)
    val_source_count = max(1, int(math.ceil(len(source_ids) * 0.2)))
    if len(source_ids) > 1:
        val_sources = set(source_ids[:val_source_count])
        train_sources = set(source_ids[val_source_count:]) or set(source_ids[:1])
    else:
        train_sources = set(source_ids)
        val_sources = set(source_ids)
    train_indexes = [index for index, (_, _, source_id) in enumerate(clips) if source_id in train_sources]
    val_indexes = [index for index, (_, _, source_id) in enumerate(clips) if source_id in val_sources]
    if not train_indexes:
        train_indexes = val_indexes[:1]
    if not val_indexes:
        val_indexes = train_indexes[:1]
    emit("log", f"按来源划分数据：训练 {len(train_indexes)} 个片段，验证 {len(val_indexes)} 个片段，来源不交叉")
    train_x, train_y = features[train_indexes], targets[train_indexes]
    val_x, val_y = features[val_indexes], targets[val_indexes]

    resume_path = str(request.get("resumeModelPath") or "").strip()
    if resume_path and Path(resume_path).is_file():
        resume_temp_dir = Path(tempfile.mkdtemp(prefix="visionworkbench_behavior_resume_"))
        resume_temp_path = resume_temp_dir / "resume.pt"
        shutil.copy2(resume_path, resume_temp_path)
        model: nn.Module = torch.jit.load(str(resume_temp_path), map_location=device)
        shutil.rmtree(resume_temp_dir, ignore_errors=True)
        emit("log", f"已加载已有行为模型：{resume_path}")
    else:
        model = BehaviorNet(len(classes))
    model.to(device)
    optimizer = torch.optim.Adam(model.parameters(), lr=0.001)
    criterion = nn.CrossEntropyLoss()
    output_dir = Path(request["outputDirectory"]).resolve() / str(request["runName"])
    output_dir.mkdir(parents=True, exist_ok=True)
    classes_path = output_dir / "classes.json"
    classes_path.write_text(json.dumps(classes, ensure_ascii=False, indent=2), encoding="utf-8")
    best_loss = float("inf")
    best_model_path = output_dir / "best.pt"
    last_model_path = output_dir / "last.pt"
    temp_model_dir = Path(tempfile.mkdtemp(prefix="visionworkbench_behavior_model_"))
    temp_best_model_path = temp_model_dir / "best.pt"
    temp_last_model_path = temp_model_dir / "last.pt"

    for epoch in range(1, epochs + 1):
        model.train()
        order = torch.randperm(len(train_x))
        train_loss = 0.0
        batches = 0
        for start in range(0, len(order), batch_size):
            batch = order[start:start + batch_size]
            optimizer.zero_grad()
            output = model(train_x[batch].to(device))
            loss = criterion(output, train_y[batch].to(device))
            loss.backward()
            optimizer.step()
            train_loss += float(loss.detach().cpu())
            batches += 1
        train_loss /= max(1, batches)
        model.eval()
        with torch.no_grad():
            val_output = model(val_x.to(device))
            val_loss = float(criterion(val_output, val_y.to(device)).cpu())
            accuracy = float((val_output.argmax(dim=1) == val_y.to(device)).float().mean().cpu())
        scripted = torch.jit.script(model.cpu())
        scripted.save(str(temp_last_model_path))
        shutil.copy2(temp_last_model_path, last_model_path)
        if val_loss <= best_loss:
            best_loss = val_loss
            scripted.save(str(temp_best_model_path))
            shutil.copy2(temp_best_model_path, best_model_path)
        model.to(device)
        emit("epoch", f"第 {epoch}/{epochs} 轮完成", epoch=epoch, totalEpochs=epochs,
             trainLoss=train_loss, valLoss=val_loss, accuracy=accuracy)

    (output_dir / "metadata.json").write_text(json.dumps({
        "classes": classes, "sequenceLength": length, "poseModel": str(request["poseModelPath"]),
        "device": device, "epochs": epochs, "batchSize": batch_size,
        "splitPolicy": "source-level",
        "trainSources": sorted(train_sources),
        "valSources": sorted(val_sources),
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    shutil.rmtree(temp_model_dir, ignore_errors=True)
    emit("completed", "行为模型已保存", modelPath=str(best_model_path), runDirectory=str(output_dir), classesPath=str(classes_path))


if __name__ == "__main__":
    try:
        raw = sys.stdin.readline()
        if not raw:
            raise ValueError("未收到训练参数。")
        main(json.loads(raw))
    except Exception as error:
        emit("error", str(error))
        raise
