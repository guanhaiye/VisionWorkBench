"""Optional temporal pose classifier for fall recognition.

The adapter accepts TorchScript ST-GCN or PoseC3D checkpoints.  A checkpoint is
deliberately optional: when it is not present, the host-side geometric fall
baseline remains active and this module only maintains the pose history.
"""

from __future__ import annotations

from collections import deque
from pathlib import Path
import shutil
import tempfile
from typing import Any

import numpy as np


class TemporalPoseClassifier:
    def __init__(
        self,
        model_type: str = "stgcn",
        model_path: str | Path | None = None,
        sequence_length: int = 30,
    ) -> None:
        normalized_type = str(model_type or "stgcn").strip().lower()
        if normalized_type not in {"stgcn", "posec3d"}:
            raise ValueError("temporalModelType must be stgcn or posec3d")
        self.model_type = normalized_type
        self.sequence_length = max(8, int(sequence_length))
        self.histories: dict[str, deque[np.ndarray]] = {}
        self.model: Any | None = None
        self.model_available = False
        self.load_error: str | None = None
        self.labels: list[str] = []
        if model_path:
            model_file = Path(model_path)
            self._load_labels(model_file)
            self._load_torchscript(model_file)

    def _load_labels(self, path: Path) -> None:
        candidates = [path.with_suffix(".json"), path.parent / "classes.json"]
        for candidate in candidates:
            if not candidate.is_file():
                continue
            try:
                import json

                payload = json.loads(candidate.read_text(encoding="utf-8-sig"))
                if isinstance(payload, list):
                    self.labels = [str(item) for item in payload]
                elif isinstance(payload, dict) and isinstance(payload.get("classes"), list):
                    self.labels = [str(item) for item in payload["classes"]]
                if self.labels:
                    return
            except Exception:
                continue

    def _load_torchscript(self, path: Path) -> None:
        if not path.is_file():
            self.load_error = f"temporal model not found: {path}"
            return
        temp_dir: Path | None = None
        try:
            import torch  # type: ignore

            # Windows 下 PyTorch/TorchScript 对中文路径仍可能报
            # "Illegal byte sequence"。复制到 ASCII 临时路径后再加载，
            # 解决行为模型位于中文数据集目录时无法启动的问题。
            temp_dir = Path(tempfile.mkdtemp(prefix="visionworkbench_temporal_"))
            temp_path = temp_dir / "model.pt"
            shutil.copy2(path, temp_path)
            self.model = torch.jit.load(str(temp_path), map_location="cpu").eval()
            self.model_available = True
        except Exception as error:  # pragma: no cover - depends on optional checkpoint/runtime
            self.model = None
            self.load_error = str(error)
        finally:
            if temp_dir is not None:
                shutil.rmtree(temp_dir, ignore_errors=True)

    def update(self, track_id: str | None, points: list[dict[str, float]]) -> dict[str, Any]:
        quality = sum(float(point.get("confidence", 0.0)) > 0.3 for point in points) / 17.0
        if not track_id or len(points) < 17:
            return {"poseQuality": round(float(quality), 6)}

        sample = np.asarray(
            [
                [
                    float(point.get("x", 0.0)),
                    float(point.get("y", 0.0)),
                    float(point.get("confidence", 0.0)),
                ]
                for point in points[:17]
            ],
            dtype=np.float32,
        )
        history = self.histories.setdefault(track_id, deque(maxlen=self.sequence_length))
        history.append(sample)
        result: dict[str, Any] = {"poseQuality": round(float(quality), 6)}
        if len(history) < self.sequence_length or not self.model_available:
            return result

        prediction = self._predict(np.asarray(history, dtype=np.float32))
        if prediction is not None:
            result.update({"temporalModel": self.model_type, **prediction})
        return result

    def _predict(self, sequence: np.ndarray) -> dict[str, Any] | None:
        if self.model is None:
            return None
        try:
            import torch  # type: ignore

            if self.model_type == "stgcn":
                # Canonical ST-GCN layout: [N, C, T, V].
                tensor = torch.from_numpy(sequence).permute(2, 0, 1).unsqueeze(0)
            else:
                tensor = torch.from_numpy(self._to_posec3d_heatmap(sequence)).unsqueeze(0)
            with torch.no_grad():
                output = self.model(tensor)
            values = output[0] if isinstance(output, (tuple, list)) else output
            values = values.detach().float().reshape(-1)
            if values.numel() == 0:
                return None
            if values.numel() > 1:
                probabilities = torch.softmax(values, dim=0)
                index = int(torch.argmax(probabilities).item())
                probability = float(probabilities[index])
            else:
                probability = float(values[0])
                if probability < 0 or probability > 1:
                    probability = float(torch.sigmoid(values[0]))
                index = 1 if probability >= 0.5 else 0
            probability = max(0.0, min(probability, 1.0))
            label = self.labels[index] if index < len(self.labels) else f"class_{index}"
            result: dict[str, Any] = {
                "behaviorClass": label,
                "behaviorProbability": round(probability, 6),
            }
            if not self.labels or "fall" in label.lower() or "跌倒" in label or "摔倒" in label:
                result["fallProbability"] = round(probability, 6)
            return result
        except Exception:
            # A bad optional checkpoint must not interrupt real-time detection.
            return None

    @staticmethod
    def _to_posec3d_heatmap(sequence: np.ndarray) -> np.ndarray:
        """Rasterize [T,17,3] normalized keypoints into [1,T,64,64]."""
        heatmap = np.zeros((1, sequence.shape[0], 64, 64), dtype=np.float32)
        for time_index, frame in enumerate(sequence):
            for x, y, confidence in frame:
                if confidence <= 0:
                    continue
                px = min(63, max(0, int(round(float(x) * 63))))
                py = min(63, max(0, int(round(float(y) * 63))))
                heatmap[0, time_index, py, px] = max(heatmap[0, time_index, py, px], confidence)
        return heatmap
