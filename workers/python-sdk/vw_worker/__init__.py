"""VisionWorkbench Python Worker SDK.

纯标准库实现，无第三方依赖。算法作者继承 :class:`AlgorithmWorker`
并实现 ``on_initialize`` / ``on_submit`` 等钩子即可接入宿主。

协议约定（文档 §13）：
- stdout 只允许一行一条 JSON 协议消息（行缓冲 + UTF-8）；
- 普通日志只能写 stderr；
- 每个请求必须有 messageId，响应必须携带 correlationId；
- 模型只在 initialize 时加载一次。
"""

from .protocol import PROTOCOL_VERSION, Message, WorkerError, send, recv, log
from .worker import AlgorithmWorker, main

__all__ = [
    "PROTOCOL_VERSION",
    "Message",
    "WorkerError",
    "send",
    "recv",
    "log",
    "AlgorithmWorker",
    "main",
]
