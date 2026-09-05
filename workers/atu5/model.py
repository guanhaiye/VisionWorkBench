from __future__ import annotations

import json
from collections import OrderedDict
from pathlib import Path
from typing import Iterable

import torch
from torch import nn
from torch.nn import functional as F
from torchvision.models import resnet50


class Conv3x3GNReLU(nn.Module):
    def __init__(self, in_channels: int, out_channels: int, upsample: bool = False):
        super().__init__()
        self.block = nn.Sequential(
            nn.Conv2d(in_channels, out_channels, 3, padding=1, bias=True),
            nn.GroupNorm(32, out_channels),
            nn.ReLU(inplace=True),
        )
        self.upsample = upsample

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        x = self.block(x)
        return F.interpolate(x, scale_factor=2, mode="bilinear", align_corners=True) if self.upsample else x


class FPNBlock(nn.Module):
    def __init__(self, pyramid_channels: int, skip_channels: int):
        super().__init__()
        self.skip_conv = nn.Conv2d(skip_channels, pyramid_channels, 1)

    def forward(self, x: torch.Tensor, skip: torch.Tensor) -> torch.Tensor:
        x = F.interpolate(x, scale_factor=2, mode="nearest")
        return x + self.skip_conv(skip)


class SegmentationBlock(nn.Module):
    def __init__(self, in_channels: int, out_channels: int, n_upsamples: int):
        super().__init__()
        blocks = [Conv3x3GNReLU(in_channels, out_channels, bool(n_upsamples))]
        for _ in range(1, n_upsamples):
            blocks.append(Conv3x3GNReLU(out_channels, out_channels, True))
        self.block = nn.Sequential(*blocks)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.block(x)


class SegmentationHead(nn.Module):
    def __init__(self, in_channels: int, out_channels: int):
        super().__init__()
        self.conv2d = nn.Conv2d(in_channels, out_channels, 1)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.conv2d(x)


class FPNDecoder(nn.Module):
    def __init__(self, encoder_channels=(3, 64, 256, 512, 1024, 2048)):
        super().__init__()
        self.p5 = nn.Conv2d(encoder_channels[-1], 256, 1)
        self.p4 = FPNBlock(256, encoder_channels[-2])
        self.p3 = FPNBlock(256, encoder_channels[-3])
        self.p2 = FPNBlock(256, encoder_channels[-4])
        self.seg_blocks = nn.ModuleList([
            SegmentationBlock(256, 128, 3),
            SegmentationBlock(256, 128, 2),
            SegmentationBlock(256, 128, 1),
            SegmentationBlock(256, 128, 0),
        ])
        self.merge = "add"
        self.dropout = nn.Dropout2d(0.2, inplace=True)

    def forward(self, features: list[torch.Tensor]) -> torch.Tensor:
        p5 = self.p5(features[-1])
        p4 = self.p4(p5, features[-2])
        p3 = self.p3(p4, features[-3])
        p2 = self.p2(p3, features[-4])
        merged = (
            self.seg_blocks[0](p5)
            + self.seg_blocks[1](p4)
            + self.seg_blocks[2](p3)
            + self.seg_blocks[3](p2)
        )
        return self.dropout(merged)


class Atu5Fpn(nn.Module):
    """ATU5 semantic model matching SunnyDlApi's FPN(ResNet50) layout."""

    def __init__(self, num_classes: int, image_size: int = 512):
        super().__init__()
        self.encoder = resnet50(weights=None)
        self.decoder = FPNDecoder()
        self.segmentation_head = SegmentationHead(128, num_classes)
        # Keep the C++ metadata available to Python checkpoints.
        self.num_classes = int(num_classes)
        self.image_size = int(image_size)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        features = [x]
        x = self.encoder.relu(self.encoder.bn1(self.encoder.conv1(x)))
        features.append(x)
        x = self.encoder.maxpool(x)
        x = self.encoder.layer1(x)
        features.append(x)
        x = self.encoder.layer2(x)
        features.append(x)
        x = self.encoder.layer3(x)
        features.append(x)
        x = self.encoder.layer4(x)
        features.append(x)
        x = self.segmentation_head(self.decoder(features))
        return F.interpolate(x, scale_factor=4, mode="bilinear", align_corners=True)


def _state_from_module(module: nn.Module) -> OrderedDict[str, torch.Tensor]:
    state = OrderedDict()
    state.update((name, value.detach().cpu()) for name, value in module.named_parameters())
    state.update((name, value.detach().cpu()) for name, value in module.named_buffers())
    return state


def load_sunny_state(path: str | Path) -> tuple[OrderedDict[str, torch.Tensor], dict]:
    """Read both C++ torch::save archives and normal Python checkpoints."""
    path = Path(path)
    try:
        loaded = torch.load(path, map_location="cpu", weights_only=False)
    except Exception:
        loaded = None
    if isinstance(loaded, dict) and "state_dict" in loaded:
        return OrderedDict(loaded["state_dict"]), dict(loaded.get("meta") or {})
    if isinstance(loaded, nn.Module):
        return _state_from_module(loaded), {}

    # torch::save(module, path) is readable as a TorchScript archive, but the
    # archive has no Python forward method. Its named tensors are still usable.
    module = torch.jit.load(str(path), map_location="cpu")
    return _state_from_module(module), {}


def read_image_size(path: str | Path, default: int = 512) -> int:
    """Read the training image size embedded in an ATU5 model or Engine."""
    metadata = read_model_metadata(path)
    value = metadata.get("imageSize", metadata.get("image_size")) if isinstance(metadata, dict) else None
    try:
        value = int(value)
        return value if value >= 32 else default
    except (TypeError, ValueError):
        return default


def read_model_metadata(path: str | Path) -> dict:
    path = Path(path)
    metadata = {}
    if path.suffix.lower() == ".engine":
        try:
            metadata = json.loads(path.with_suffix(".engine.meta.json").read_text(encoding="utf-8"))
        except (OSError, ValueError, TypeError):
            metadata = {}
    else:
        try:
            loaded = torch.load(path, map_location="cpu", weights_only=False)
            if isinstance(loaded, dict):
                metadata = loaded.get("meta") or {}
        except Exception:
            metadata = {}
    return metadata if isinstance(metadata, dict) else {}


def load_model(path: str | Path, device: torch.device, num_classes: int, image_size: int) -> Atu5Fpn:
    state, meta = load_sunny_state(path)
    classes = int(meta.get("numClasses", meta.get("num_classes", num_classes)))
    size = int(meta.get("imageSize", meta.get("image_size", image_size)))
    model = Atu5Fpn(classes, size)
    # A full segmentor checkpoint has decoder/head keys. atu5.pt only has the
    # ResNet50 encoder, which is intentionally accepted as pretraining input.
    own = model.state_dict()
    compatible = {k: v for k, v in state.items() if k in own and own[k].shape == v.shape}
    model.load_state_dict(compatible, strict=False)
    return model.to(device).eval()


def initialize_for_training(path: str | Path | None, device: torch.device, num_classes: int, image_size: int) -> Atu5Fpn:
    model = Atu5Fpn(num_classes, image_size).to(device)
    if path and Path(path).is_file():
        state, _ = load_sunny_state(path)
        own = model.state_dict()
        compatible = {
            (k if k.startswith("encoder.") else f"encoder.{k}"): v
            for k, v in state.items()
            if (k if k.startswith("encoder.") else f"encoder.{k}") in own
            and own[k if k.startswith("encoder.") else f"encoder.{k}"].shape == v.shape
        }
        model.load_state_dict(compatible, strict=False)
    return model
