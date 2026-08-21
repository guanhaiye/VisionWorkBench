# YOLOE 智能标注适配器

`worker.py` 接收参考图上的归一化矩形框，并使用 Ultralytics YOLOE 的 visual prompt segmentation API 对当前图或后续图片执行推理。每个目标返回多边形掩码；无法取得掩码时返回矩形框。

默认模型路径为 `models/yoloe-11s-seg.pt`。模型文件和 Python 虚拟环境不提交到 Git，请在应用设置中配置模型路径和解释器。
