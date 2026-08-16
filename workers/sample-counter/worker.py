"""sample-counter 插件入口：通用物体计数（静态计数）。

用法（开发态）：
    python worker.py
宿主通过 JSON Lines 协议驱动；独立运行时每 5 秒输出一次心跳日志。
"""

from __future__ import annotations

import sys
import uuid
from pathlib import Path
from typing import Any

# 开发态直接引用仓库内 python-sdk，无需安装。
_SDK_PATH = Path(__file__).resolve().parent.parent / "python-sdk"
if str(_SDK_PATH) not in sys.path:
    sys.path.insert(0, str(_SDK_PATH))

from vw_worker import AlgorithmWorker, WorkerError, log, main  # noqa: E402

from counter import SimpleCounter  # noqa: E402


class CounterWorker(AlgorithmWorker):
    plugin_id = "com.vision.sample-counter"
    worker_version = "1.0.0"

    def __init__(self) -> None:
        super().__init__()
        self._counter: SimpleCounter | None = None

    def on_initialize(self, payload: dict) -> dict:
        settings = payload.get("settings") or {}
        try:
            self._counter = SimpleCounter(settings)
        except ValueError as error:
            # 参数不符合 schema → SETTINGS_INVALID
            raise WorkerError("SETTINGS_INVALID", str(error)) from error
        self.settings = self._counter.settings
        return {
            "success": True,
            "loadedModel": "simple-counter/adaptive-threshold",
            "device": "cpu",
        }

    def on_submit(self, payload: dict) -> dict:
        if self._counter is None:
            raise WorkerError("MODEL_NOT_FOUND", "算法尚未初始化")
        image_path = payload.get("imagePath")
        if not image_path:
            raise WorkerError("INPUT_INVALID", "submit 负载缺少 imagePath")
        try:
            result = self._counter.process(image_path)
        except FileNotFoundError as error:
            raise WorkerError("MODEL_NOT_FOUND", str(error)) from error
        except ValueError as error:
            raise WorkerError("IMAGE_DECODE_FAILED", str(error)) from error

        return {
            "outputId": "out-" + uuid.uuid4().hex[:12],
            "inputId": payload.get("inputId", ""),
            "sequence": int(payload.get("frameSequence", 0)),
            "detections": result["detections"],
            "metrics": result["metrics"],
            "performance": result["performance"],
        }


if __name__ == "__main__":
    log("sample-counter worker 启动")
    raise SystemExit(main(CounterWorker()))
