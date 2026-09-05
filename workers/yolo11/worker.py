"""VisionWorkbench YOLO11 Worker。"""

from __future__ import annotations

import sys
import uuid
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parent
SDK_PATH = PLUGIN_DIR.parent / "python-sdk"
if str(SDK_PATH) not in sys.path:
    sys.path.insert(0, str(SDK_PATH))

from vw_worker import AlgorithmWorker, WorkerError, log, main  # noqa: E402

from yolo11 import Yolo11Engine  # noqa: E402


class Yolo11Worker(AlgorithmWorker):
    plugin_id = "com.vision.yolo11"
    worker_version = "1.0.0"

    def __init__(self) -> None:
        super().__init__()
        self._engine: Yolo11Engine | None = None

    def on_initialize(self, payload: dict) -> dict:
        try:
            self._engine = Yolo11Engine(payload.get("settings") or {}, PLUGIN_DIR)
        except FileNotFoundError as error:
            raise WorkerError("MODEL_NOT_FOUND", str(error)) from error
        except ImportError as error:
            raise WorkerError("RUNTIME_NOT_FOUND", str(error)) from error
        except RuntimeError as error:
            raise WorkerError("RUNTIME_NOT_FOUND", str(error)) from error
        except ValueError as error:
            raise WorkerError("SETTINGS_INVALID", str(error)) from error

        self.settings = self._engine.settings.as_dict()
        return {
            "success": True,
            "loadedModel": str(self._engine.settings.model_path),
            "device": self._engine.device,
        }

    def on_submit(self, payload: dict) -> dict:
        if self._engine is None:
            raise WorkerError("MODEL_NOT_FOUND", "YOLO11 模型尚未初始化")
        image_path = payload.get("imagePath")
        if not image_path:
            raise WorkerError("INPUT_INVALID", "submit 负载缺少 imagePath")
        try:
            output = self._engine.predict(
                str(image_path), int(payload.get("frameSequence", 0)), payload.get("roi")
            )
        except FileNotFoundError as error:
            raise WorkerError("INPUT_INVALID", str(error)) from error
        except ValueError as error:
            raise WorkerError("IMAGE_DECODE_FAILED", str(error)) from error
        except OSError as error:
            raise WorkerError("OUTPUT_WRITE_FAILED", str(error)) from error

        return {
            "outputId": "out-" + uuid.uuid4().hex[:12],
            "inputId": payload.get("inputId", ""),
            "sequence": int(payload.get("frameSequence", 0)),
            **output,
        }


if __name__ == "__main__":
    log("YOLO11 worker 启动")
    raise SystemExit(main(Yolo11Worker()))
