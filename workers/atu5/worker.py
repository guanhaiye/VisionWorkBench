"""ATU5/FPN semantic segmentation realtime worker."""
from __future__ import annotations

import sys
import uuid
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parent
SDK_PATH = PLUGIN_DIR.parent / "python-sdk"
if str(SDK_PATH) not in sys.path:
    sys.path.insert(0, str(SDK_PATH))

# The host's shared workers environment is intentionally lightweight.  ATU5
# needs the heavyweight PyTorch runtime already installed for YOLO11, so keep
# the worker usable even when an older host starts it with workers/.venv.
SHARED_SITE_PACKAGES = PLUGIN_DIR.parent / "yolo11" / ".venv" / "Lib" / "site-packages"
if SHARED_SITE_PACKAGES.is_dir() and str(SHARED_SITE_PACKAGES) not in sys.path:
    sys.path.insert(0, str(SHARED_SITE_PACKAGES))

from vw_worker import AlgorithmWorker, WorkerError, log, main  # noqa: E402

from model import read_image_size  # noqa: E402
from test_worker import _device, prepare_model, run  # noqa: E402


class Atu5Worker(AlgorithmWorker):
    plugin_id = "com.vision.atu5"
    worker_version = "1.0.0"

    def __init__(self) -> None:
        super().__init__()
        self._model_path: Path | None = None
        self._device_value = "cpu"
        self._loaded_model = None
        self._engine_runner = None
        self._min_area = 1

    def on_initialize(self, payload: dict) -> dict:
        settings = dict(payload.get("settings") or {})
        model_value = str(settings.get("modelPath", "models/atu5.pt")).strip() or "models/atu5.pt"
        model_path = Path(model_value)
        if not model_path.is_absolute():
            model_path = PLUGIN_DIR / model_path
        model_path = model_path.resolve()
        if not model_path.is_file():
            raise WorkerError("MODEL_NOT_FOUND", f"ATU5 模型不存在：{model_path}")

        configured_device = str(settings.get("device") or payload.get("executionProvider") or "auto")
        if configured_device == "cuda":
            configured_device = "cuda:0"
        try:
            device = _device(configured_device)
            image_size = read_image_size(model_path)
            self._loaded_model, self._engine_runner = prepare_model(model_path, device, image_size)
        except FileNotFoundError as error:
            raise WorkerError("MODEL_NOT_FOUND", str(error)) from error
        except ImportError as error:
            raise WorkerError("RUNTIME_NOT_FOUND", str(error)) from error
        except (RuntimeError, ValueError) as error:
            raise WorkerError("MODEL_LOAD_FAILED", str(error)) from error

        self.settings = settings
        self.settings["task"] = "semantic"
        self.settings["modelPath"] = str(model_path)
        self._model_path = model_path
        self._device_value = str(device)
        self._min_area = max(1, int(settings.get("minArea", 1)))
        return {
            "success": True,
            "loadedModel": str(model_path),
            "device": self._device_value,
        }

    def on_submit(self, payload: dict) -> dict:
        if self._model_path is None:
            raise WorkerError("MODEL_NOT_FOUND", "ATU5 模型尚未初始化")
        image_path = payload.get("imagePath")
        if not image_path:
            raise WorkerError("INPUT_INVALID", "submit 负载缺少 imagePath")
        request = dict(payload)
        request["modelPath"] = str(self._model_path)
        request["device"] = self._device_value
        request["minArea"] = self._min_area
        try:
            output = run(request, self._loaded_model, self._engine_runner)
        except FileNotFoundError as error:
            raise WorkerError("INPUT_INVALID", str(error)) from error
        except ValueError as error:
            raise WorkerError("IMAGE_DECODE_FAILED", str(error)) from error
        except (ImportError, RuntimeError) as error:
            raise WorkerError("INFERENCE_FAILED", str(error)) from error

        segmentations = []
        for mask in output.get("masks", []):
            polygon = mask.get("polygon") or []
            if len(polygon) < 3:
                continue
            segmentations.append({
                "mode": "semantic",
                "classId": str(mask.get("classId", "")),
                "confidence": float(mask.get("confidence", 1.0)),
                "contours": [polygon],
                "areaRatio": float(mask.get("areaRatio", 0.0)),
            })
        return {
            "outputId": "out-" + uuid.uuid4().hex[:12],
            "inputId": payload.get("inputId", ""),
            "sequence": int(payload.get("frameSequence", 0)),
            "imageWidth": int(output.get("imageWidth", 0)),
            "imageHeight": int(output.get("imageHeight", 0)),
            "detections": [],
            "segmentations": segmentations,
            "metrics": [{"name": "count", "value": len(segmentations)}],
            "classNames": output.get("classNames", []),
            "task": "semantic",
        }


if __name__ == "__main__":
    log("ATU5/FPN semantic worker started")
    raise SystemExit(main(Atu5Worker()))
