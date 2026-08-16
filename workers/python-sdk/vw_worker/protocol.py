"""JSON Lines 协议收发与错误定义。"""

from __future__ import annotations

import json
import sys
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Optional

PROTOCOL_VERSION = "1.0"

# 关键：stdout 在管道下默认块缓冲，必须显式配置行缓冲 + UTF-8，
# 否则宿主收不到消息或中文乱码（GBK 控制台机器）。
sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
sys.stderr.reconfigure(encoding="utf-8", line_buffering=True)


class WorkerError(Exception):
    """携带标准错误码的算法异常（文档 §22.2）。"""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


@dataclass
class Message:
    type: str
    message_id: str = field(default_factory=lambda: "msg-" + uuid.uuid4().hex[:12])
    correlation_id: Optional[str] = None
    payload: Any = None
    protocol_version: str = PROTOCOL_VERSION
    timestamp: str = field(
        default_factory=lambda: datetime.now(timezone.utc).isoformat()
    )

    @classmethod
    def from_dict(cls, data: dict) -> "Message":
        return cls(
            type=data.get("type", ""),
            message_id=data.get("messageId", ""),
            correlation_id=data.get("correlationId"),
            payload=data.get("payload"),
            protocol_version=data.get("protocolVersion", PROTOCOL_VERSION),
        )


def new_message_id() -> str:
    return "msg-" + uuid.uuid4().hex[:12]


def send(
    type_: str,
    payload: Any = None,
    message_id: Optional[str] = None,
    correlation_id: Optional[str] = None,
) -> None:
    """向宿主发送一条协议消息（单行 JSON）。"""
    envelope = {
        "protocolVersion": PROTOCOL_VERSION,
        "type": type_,
        "messageId": message_id or new_message_id(),
        "correlationId": correlation_id,
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "payload": payload,
    }
    line = json.dumps(envelope, ensure_ascii=False, default=str)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def recv() -> Optional[Message]:
    """阻塞读取一条宿主消息；stdin 关闭（宿主退出）返回 None。"""
    while True:
        line = sys.stdin.readline()
        if not line:
            return None
        line = line.strip().lstrip("\ufeff")
        if not line:
            continue
        try:
            data = json.loads(line)
        except json.JSONDecodeError:
            log("收到非法 JSON 行，已忽略: %r" % line[:200])
            continue
        if not isinstance(data, dict) or not data.get("type"):
            log("收到缺少 type 的消息，已忽略: %r" % line[:200])
            continue
        return Message.from_dict(data)


def log(message: str) -> None:
    """算法日志 → stderr（永不出现在 stdout，PLG-008）。"""
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    sys.stderr.write("[%s] %s\n" % (stamp, message))
    sys.stderr.flush()
