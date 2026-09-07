"""Train the SunnyDlApi ATU5/FPN semantic segmentation model."""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import cv2
import numpy as np
import torch
from torch import nn
from torch.nn import functional as F
from torch.utils.data import DataLoader, Dataset

from export_worker import export_engine
from model import initialize_for_training, load_sunny_state


def emit(event: str, message: str = "", **values) -> None:
    payload = {"event": event, "message": message, **values}
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def select_device(value: str) -> torch.device:
    if value == "auto":
        return torch.device("cuda:0" if torch.cuda.is_available() else "cpu")
    return torch.device(value if value != "0" else "cuda:0")


class SemanticDataset(Dataset):
    def __init__(self, items: list[dict], classes: list[str], image_size: int):
        self.items = items
        # Class 0 is reserved for background. Manifest classes contain only
        # foreground classes, so their target ids start at 1.
        self.class_ids = {name.casefold(): index + 1 for index, name in enumerate(classes)}
        self.image_size = image_size

    def __len__(self) -> int:
        return len(self.items)

    def __getitem__(self, index: int):
        item = self.items[index]
        try:
            encoded = np.fromfile(str(item["imagePath"]), dtype=np.uint8)
            image = cv2.imdecode(encoded, cv2.IMREAD_COLOR) if encoded.size else None
        except OSError:
            image = None
        if image is None:
            raise ValueError(f"无法读取训练图片：{item['imagePath']}")
        height, width = image.shape[:2]
        image = cv2.cvtColor(cv2.resize(image, (self.image_size, self.image_size)), cv2.COLOR_BGR2RGB)
        mask = np.zeros((height, width), dtype=np.uint8)
        for obj in item.get("objects") or []:
            class_id = self.class_ids.get(str(obj.get("className", "")).casefold())
            if class_id is None:
                continue
            polygon = obj.get("polygon") or []
            if len(polygon) >= 3:
                points = np.array([[round(float(p.get("x", 0)) * width), round(float(p.get("y", 0)) * height)]
                                   for p in polygon], dtype=np.int32)
                cv2.fillPoly(mask, [points], int(class_id))
            else:
                x = round(float(obj.get("x", 0)) * width)
                y = round(float(obj.get("y", 0)) * height)
                w = round(float(obj.get("width", 0)) * width)
                h = round(float(obj.get("height", 0)) * height)
                cv2.rectangle(mask, (x, y), (x + w, y + h), int(class_id), -1)
        mask = cv2.resize(mask, (self.image_size, self.image_size), interpolation=cv2.INTER_NEAREST)
        tensor = torch.from_numpy(image).permute(2, 0, 1).float().div(255.0)
        mean = torch.tensor([0.485, 0.456, 0.406]).view(3, 1, 1)
        std = torch.tensor([0.229, 0.224, 0.225]).view(3, 1, 1)
        return (tensor - mean) / std, torch.from_numpy(mask).long()


def dice_loss(logits: torch.Tensor, target: torch.Tensor, classes: int) -> torch.Tensor:
    probabilities = logits.softmax(1)
    one_hot = F.one_hot(target, classes).permute(0, 3, 1, 2).float()
    intersection = (probabilities * one_hot).sum((0, 2, 3))
    denominator = probabilities.sum((0, 2, 3)) + one_hot.sum((0, 2, 3))
    return (1 - (2 * intersection + 1) / (denominator + 1)).mean()


def main() -> int:
    try:
        request = json.loads(sys.stdin.read().lstrip("\ufeff").strip())
        manifest_path = Path(str(request["manifestPath"])).resolve()
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        classes = [str(x) for x in manifest.get("classes") or []]
        if not classes:
            raise ValueError("语义分割至少需要背景和一个前景类别")
        num_classes = len(classes) + 1  # background + foreground classes
        image_size = int(request.get("imageSize", 512))
        epochs = int(request.get("epochs", 100))
        batch_size = int(request.get("batchSize", 2))
        device = select_device(str(request.get("device", "auto")))
        items = [item for item in manifest.get("items", []) if Path(str(item.get("imagePath", ""))).is_file()]
        train_items = [item for item in items if item.get("split") == "train"]
        val_items = [item for item in items if item.get("split") == "val"]
        if not train_items:
            raise ValueError("语义分割训练集为空，请先自动划分已标注图片")
        if not val_items:
            val_items = train_items[: max(1, min(4, len(train_items)))]

        pretrained = str(request.get("pretrainedPath", "")).strip() or None
        model = initialize_for_training(pretrained, device, num_classes, image_size)
        if pretrained and Path(pretrained).is_file():
            state, meta = load_sunny_state(pretrained)
            if any(key.startswith("decoder.") for key in state):
                own = model.state_dict()
                model.load_state_dict({k: v for k, v in state.items() if k in own and own[k].shape == v.shape}, strict=False)
        train_loader = DataLoader(SemanticDataset(train_items, classes, image_size), batch_size=batch_size,
                                  shuffle=True, num_workers=0, pin_memory=device.type == "cuda")
        val_loader = DataLoader(SemanticDataset(val_items, classes, image_size), batch_size=1,
                                shuffle=False, num_workers=0)
        optimizer = torch.optim.AdamW(model.parameters(), lr=float(request.get("learningRate", 1e-4)), weight_decay=1e-4)
        criterion = nn.CrossEntropyLoss()
        output_dir = Path(str(request["outputDirectory"])).resolve()
        run_dir = output_dir / str(request.get("runName", f"atu5-{int(time.time())}"))
        run_dir.mkdir(parents=True, exist_ok=True)
        pause_file = str(request.get("pauseFilePath") or "").strip()

        def wait_if_paused() -> None:
            while pause_file and Path(pause_file).is_file():
                time.sleep(0.2)

        best_iou = -1.0
        emit("starting", f"ATU5 语义分割训练已启动，设备：{device}", totalEpochs=epochs)
        for epoch in range(1, epochs + 1):
            wait_if_paused()
            model.train()
            losses = []
            for images, masks in train_loader:
                images, masks = images.to(device), masks.to(device)
                optimizer.zero_grad(set_to_none=True)
                logits = model(images)
                loss = criterion(logits, masks) + 0.5 * dice_loss(logits, masks, num_classes)
                loss.backward()
                optimizer.step()
                losses.append(float(loss.detach().cpu()))
            model.eval()
            intersection = torch.zeros(num_classes, dtype=torch.float64)
            union = torch.zeros(num_classes, dtype=torch.float64)
            with torch.inference_mode():
                for images, masks in val_loader:
                    prediction = model(images.to(device)).argmax(1).cpu()
                    for class_id in range(1, num_classes):
                        predicted = prediction == class_id
                        target = masks == class_id
                        intersection[class_id] += torch.logical_and(predicted, target).sum()
                        union[class_id] += torch.logical_or(predicted, target).sum()
            # Report foreground mIoU; background pixels should not dominate it.
            valid = union[1:] > 0
            miou = float((intersection[1:][valid] / union[1:][valid].clamp_min(1)).mean()) if valid.any() else 0.0
            loss_value = float(np.mean(losses)) if losses else 0.0
            emit("epoch", f"Epoch {epoch}/{epochs}  Loss={loss_value:.5f}  mIoU={miou:.4f}",
                 epoch=epoch, totalEpochs=epochs, trainLoss=loss_value, valLoss=1 - miou)
            checkpoint = {"state_dict": model.state_dict(), "meta": {"numClasses": num_classes, "imageSize": image_size, "classes": ["background", *classes]}}
            torch.save(checkpoint, run_dir / "last.pt")
            if miou >= best_iou:
                best_iou = miou
                torch.save(checkpoint, run_dir / "best.pt")
        model_path = run_dir / "best.pt"
        (run_dir / "classes.json").write_text(json.dumps(["background", *classes], ensure_ascii=False, indent=2), encoding="utf-8")
        engine_path = None
        if bool(request.get("exportTensorRt", True)):
            emit("log", "训练完成，正在导出 TensorRT Engine…")
            try:
                engine_path = export_engine(model_path, image_size)
                emit("log", f"TensorRT Engine 已导出：{engine_path}")
            except Exception as export_error:  # noqa: BLE001
                emit("log", f"模型训练成功，但 TensorRT Engine 导出失败：{export_error}")
        emit("completed", f"ATU5 训练完成，最佳 mIoU={best_iou:.4f}",
             modelPath=str(model_path), runDirectory=str(run_dir), enginePath=engine_path, bestMiou=best_iou)
        return 0
    except Exception as error:
        emit("error", str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
