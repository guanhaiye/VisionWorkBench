"""通用物体计数（静态计数，文档 §16.2）。

首版使用 OpenCV 传统视觉：灰度 → 自适应阈值 → 形态学开闭 → 轮廓过滤。
算法输出事实（检测框与数量），业务判定由宿主规则引擎完成。
"""

from __future__ import annotations

import math
import time
from typing import Any

import cv2
import numpy as np

DEFAULT_SETTINGS = {
    "minAreaPx": 120,       # 最小目标面积（像素）
    "maxAreaPx": 0,         # 最大目标面积（0 = 不限制）
    "thresholdBlock": 31,   # 自适应阈值窗口（奇数）
    "thresholdC": -12,      # 阈值偏移：T = 局部均值 - C；亮目标用负值，暗目标用正值
    "morphKernel": 3,       # 形态学核大小
    "invert": False,        # 目标比背景暗时置 true
    "classId": "object",
    "className": "物体",
}


class SimpleCounter:
    def __init__(self, settings: dict[str, Any] | None = None):
        merged: dict[str, Any] = dict(DEFAULT_SETTINGS)
        merged.update(settings or {})
        self._validate(merged)
        self.settings = merged
        self.device = "cpu"

    @staticmethod
    def _validate(settings: dict[str, Any]) -> None:
        block = int(settings["thresholdBlock"])
        if block < 3 or block > 401 or block % 2 == 0:
            raise ValueError("thresholdBlock 必须为 3~401 之间的奇数")
        kernel = int(settings["morphKernel"])
        if kernel < 1 or kernel > 31:
            raise ValueError("morphKernel 必须为 1~31")
        if int(settings["minAreaPx"]) < 1:
            raise ValueError("minAreaPx 必须 ≥ 1")

    def process(self, image_path: str) -> dict[str, Any]:
        """对单张图片执行检测，返回 AlgorithmOutput 形状的字典。"""
        started = time.perf_counter()
        data = np.fromfile(image_path, dtype=np.uint8)
        if data.size == 0:
            raise FileNotFoundError("图像文件为空或不存在: %s" % image_path)
        image = cv2.imdecode(data, cv2.IMREAD_COLOR)
        if image is None:
            # SEC-004：按真实内容验证，伪装扩展名直接拒绝
            raise ValueError("图像解码失败（文件内容不是有效图片）: %s" % image_path)

        height, width = image.shape[:2]
        inference_started = time.perf_counter()

        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        # 中值滤波对背景噪声的抑制优于高斯（保边去噪）
        gray = cv2.medianBlur(gray, 5)
        block = int(self.settings["thresholdBlock"])
        thresh = cv2.adaptiveThreshold(
            gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, block,
            int(self.settings["thresholdC"]),
        )
        if bool(self.settings["invert"]):
            thresh = cv2.bitwise_not(thresh)

        kernel = int(self.settings["morphKernel"])
        morph = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel, kernel))
        mask = cv2.morphologyEx(thresh, cv2.MORPH_OPEN, morph, iterations=2)
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, morph, iterations=2)

        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        min_area = float(self.settings["minAreaPx"])
        max_area = float(self.settings["maxAreaPx"])

        detections = []
        for contour in contours:
            area = cv2.contourArea(contour)
            if area < min_area:
                continue
            if max_area > 0 and area > max_area:
                continue
            x, y, w, h = cv2.boundingRect(contour)
            if w == 0 or h == 0:
                continue
            aspect = w / h
            if aspect < 0.1 or aspect > 10.0:
                continue
            confidence = _area_confidence(area, min_area)
            detections.append(
                {
                    "classId": self.settings["classId"],
                    "className": self.settings["className"],
                    "confidence": round(confidence, 3),
                    "box": {
                        "x": x / width,
                        "y": y / height,
                        "width": w / width,
                        "height": h / height,
                    },
                }
            )

        detections.sort(key=lambda d: (d["box"]["y"], d["box"]["x"]))
        inference_ms = (time.perf_counter() - inference_started) * 1000.0
        total_ms = (time.perf_counter() - started) * 1000.0

        return {
            "detections": detections,
            "metrics": [
                {"name": "count", "value": len(detections), "unit": "个"},
            ],
            "performance": {
                "inferenceMs": round(inference_ms, 2),
                "totalMs": round(total_ms, 2),
                "device": self.device,
            },
        }


def _area_confidence(area: float, min_area: float) -> float:
    """面积越大越可信：[min_area, 4*min_area] 映射到 [0.55, 0.99]。"""
    ratio = min(area / max(min_area, 1.0), 4.0)
    return 0.55 + 0.44 * (math.sqrt(ratio) / 2.0)
