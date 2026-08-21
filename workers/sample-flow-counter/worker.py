"""sample-flow-counter 插件入口：流水线计数（动态去重 / 跨线，文档 §16.3/§16.4）。

有状态算法：on_start_session 重建跟踪状态；counter_command reset 清除
轨迹与去重记忆（宿主计数清零的唯一配套入口，CNT-L-012）。
"""

from __future__ import annotations

import sys
import uuid
from pathlib import Path
from typing import Any

# 开发态直接引用仓库内 python-sdk 与 sample-counter 检测内核，无需安装。
_PLUGIN_DIR = Path(__file__).resolve().parent
for _rel in ("../python-sdk", "../sample-counter"):
    _path = (_PLUGIN_DIR / _rel).resolve()
    if str(_path) not in sys.path:
        sys.path.insert(0, str(_path))

from vw_worker import AlgorithmWorker, WorkerError, log, main  # noqa: E402

from flow_counter import FlowCounter  # noqa: E402


class FlowCounterWorker(AlgorithmWorker):
    plugin_id = "com.vision.sample-flow-counter"
    worker_version = "1.0.0"

    def __init__(self) -> None:
        super().__init__()
        self._flow: FlowCounter | None = None

    def on_initialize(self, payload: dict) -> dict:
        settings = payload.get("settings") or {}
        try:
            self._flow = FlowCounter(settings)
        except ValueError as error:
            raise WorkerError("SETTINGS_INVALID", str(error)) from error
        self.settings = self._flow.settings
        return {
            "success": True,
            "loadedModel": "flow-counter/iou-tracker",
            "device": "cpu",
        }

    def on_start_session(self, payload: dict) -> dict:
        # 每个会话独立的跟踪状态（文档 §15：动态计数必须维护会话状态）
        if self._flow is not None:
            self._flow.reset_session()
        return {"ack": True, "sessionId": payload.get("sessionId")}

    def on_submit(self, payload: dict) -> dict:
        if self._flow is None:
            raise WorkerError("MODEL_NOT_FOUND", "算法尚未初始化")
        image_path = payload.get("imagePath")
        if not image_path:
            raise WorkerError("INPUT_INVALID", "submit 负载缺少 imagePath")
        try:
            result = self._flow.process(
                image_path, int(payload.get("frameSequence", 0)))
        except FileNotFoundError as error:
            raise WorkerError("MODEL_NOT_FOUND", str(error)) from error
        except ValueError as error:
            raise WorkerError("IMAGE_DECODE_FAILED", str(error)) from error

        return {
            "outputId": "out-" + uuid.uuid4().hex[:12],
            "inputId": payload.get("inputId", ""),
            "sequence": int(payload.get("frameSequence", 0)),
            "detections": result["detections"],
            "tracks": result["tracks"],
            "countingEvents": result["countingEvents"],
            "metrics": result["metrics"],
            "performance": result["performance"],
        }

    def on_counter_command(self, payload: dict) -> dict:
        command = str(payload.get("command") or "").strip().lower()
        if command == "reset" and self._flow is not None:
            self._flow.reset_session()
            return {"ack": True, "handled": True}
        return {"ack": True, "handled": False}


if __name__ == "__main__":
    log("sample-flow-counter worker 启动")
    raise SystemExit(main(FlowCounterWorker()))
