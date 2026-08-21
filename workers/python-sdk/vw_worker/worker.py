"""算法 Worker 基类：生命周期消息循环（文档 §13.4）。

生命周期：hello → initialize/ready → start_session → submit(result/event)
          → flush → stop_session → shutdown
"""

from __future__ import annotations

import time
from typing import Any, Optional

from .protocol import Message, WorkerError, log, send


class AlgorithmWorker:
    """算法插件基类。子类至少实现 on_initialize 与 on_submit。"""

    plugin_id = "unknown-plugin"
    worker_version = "0.0.1"

    def __init__(self) -> None:
        self._started_at = time.monotonic()
        self.settings: dict[str, Any] = {}

    # ---- 生命周期钩子（子类按需覆盖） --------------------------------

    def on_initialize(self, payload: dict) -> dict:
        """加载模型与资源（只执行一次）。返回 ready 负载。"""
        self.settings = dict(payload.get("settings") or {})
        return {"success": True, "loadedModel": None, "device": "cpu"}

    def on_start_session(self, payload: dict) -> dict:
        """创建算法会话。有状态算法在此初始化跟踪器等。"""
        return {"ack": True, "sessionId": payload.get("sessionId")}

    def on_submit(self, payload: dict) -> dict:
        """执行一次推理。payload 见 AlgorithmInput；返回 AlgorithmOutput 负载。"""
        raise NotImplementedError

    def on_update_settings(self, payload: dict) -> dict:
        self.settings.update(payload.get("settings") or {})
        return {"ack": True}

    def on_flush(self, payload: dict) -> dict:
        """输出缓冲中的未决结果（流式算法用）。"""
        return {"ack": True}

    def on_counter_command(self, payload: dict) -> dict:
        """计数器命令（reset/pause/resume）。有状态算法在此清理跟踪记忆。"""
        return {"ack": True}

    def on_stop_session(self, payload: dict) -> dict:
        return {"ack": True}

    def on_shutdown(self, payload: dict) -> dict:
        return {"ack": True}

    # ---- 消息循环 ----------------------------------------------------

    def run(self) -> int:
        send(
            "hello",
            {
                "pluginId": self.plugin_id,
                "workerVersion": self.worker_version,
                "protocolVersion": "1.0",
            },
        )
        log("worker %s %s 已启动，等待宿主指令" % (self.plugin_id, self.worker_version))
        while True:
            message = self.__class__._recv()
            if message is None:
                log("stdin 已关闭，worker 退出")
                return 0
            try:
                if not self._dispatch(message):
                    return 0
            except WorkerError as error:
                self._respond_error(message, error.code, str(error))
            except Exception as error:  # noqa: BLE001 - 顶层兜底，不能崩溃进程
                log("处理 %s 失败: %s" % (message.type, error))
                self._respond_error(message, "INFERENCE_FAILED", str(error))

    @staticmethod
    def _recv() -> Optional[Message]:
        from .protocol import recv

        return recv()

    def _dispatch(self, message: Message) -> bool:
        """返回 False 表示应退出消息循环。"""
        payload = message.payload if isinstance(message.payload, dict) else {}

        if message.type == "initialize":
            result = self.on_initialize(payload)
            self._respond(message, "ready", result)
        elif message.type == "start_session":
            self._respond(message, "result", self.on_start_session(payload))
        elif message.type == "submit":
            started = time.perf_counter()
            result = self.on_submit(payload)
            result.setdefault("performance", self._performance(started))
            self._respond(message, "result", result)
        elif message.type == "update_settings":
            self._respond(message, "result", self.on_update_settings(payload))
        elif message.type == "counter_command":
            self._respond(message, "result", self.on_counter_command(payload))
        elif message.type == "health":
            self._respond(
                message,
                "result",
                {
                    "state": "running",
                    "uptimeSeconds": round(time.monotonic() - self._started_at, 1),
                    "pluginId": self.plugin_id,
                },
            )
        elif message.type == "flush":
            self._respond(message, "result", self.on_flush(payload))
        elif message.type == "stop_session":
            self._respond(message, "result", self.on_stop_session(payload))
        elif message.type == "cancel":
            self._respond(message, "result", {"ack": True})
        elif message.type == "shutdown":
            self._respond(message, "result", self.on_shutdown(payload))
            log("收到 shutdown，正常退出")
            return False
        else:
            self._respond_error(
                message, "PLUGIN_INVALID", "未知消息类型: %s" % message.type
            )
        return True

    def _performance(self, started: float) -> dict:
        total_ms = (time.perf_counter() - started) * 1000.0
        return {
            "totalMs": round(total_ms, 2),
            "inferenceMs": round(total_ms, 2),
            "device": "cpu",
        }

    def _respond(self, request: Message, type_: str, payload: Any) -> None:
        send(type_, payload, correlation_id=request.message_id)

    def _respond_error(self, request: Message, code: str, message: str) -> None:
        send(
            "error",
            {"code": code, "message": message},
            correlation_id=request.message_id,
        )


def main(worker: AlgorithmWorker) -> int:
    try:
        return worker.run()
    except Exception as error:  # noqa: BLE001
        log("worker 致命错误: %s" % error)
        return 1
