"""生成冻结测试数据（文档 §3.2 / §4.4）。

输出到 samples/：
- static-count/    静态计数图片集 + truth.json（数量真值）
- conveyor/        流水线视频（供 VideoFileProvider 与后续跨线计数使用）

用法：python tools/make_test_images.py [--seed 42]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

REPO = Path(__file__).resolve().parent.parent
OUT_STATIC = REPO / "samples" / "static-count"
OUT_VIDEO = REPO / "samples" / "conveyor"


def make_image(count: int, rng: np.random.Generator, size=(800, 600)) -> np.ndarray:
    width, height = size
    image = np.full((height, width, 3), 108, dtype=np.uint8)
    noise = rng.normal(0, 4, image.shape).astype(np.int16)
    image = np.clip(image.astype(np.int16) + noise, 0, 255).astype(np.uint8)

    placed: list[tuple[int, int, int]] = []  # (cx, cy, reach)

    def try_place():
        for _ in range(300):
            radius = int(rng.integers(16, 34))
            cx = int(rng.integers(radius + 10, width - radius - 10))
            cy = int(rng.integers(radius + 10, height - radius - 10))
            reach = int(radius * 1.7)
            if all((cx - px) ** 2 + (cy - py) ** 2 > (reach + pr + 12) ** 2 for px, py, pr in placed):
                return cx, cy, radius
        return None

    for i in range(count):
        spot = try_place()
        if spot is None:
            break  # 画布放不下则提前结束（真值以实际放置数为准）
        cx, cy, radius = spot
        placed.append((cx, cy, int(radius * 1.7)))
        color = int(rng.integers(185, 240))
        if i % 2 == 0:
            cv2.circle(image, (cx, cy), radius, (color, color, color), -1)
        else:
            w = int(radius * 1.7)
            h = int(radius * 1.4)
            cv2.rectangle(
                image,
                (cx - w // 2, cy - h // 2),
                (cx + w // 2, cy + h // 2),
                (color, color, color),
                -1,
            )
    return image, len(placed)


def make_overlap_image(rng: np.random.Generator, size=(800, 600)) -> np.ndarray:
    """两个部分重叠的圆（CNT-S-005 重叠样本）。"""
    width, height = size
    image = np.full((height, width, 3), 108, dtype=np.uint8)
    cv2.circle(image, (300, 280), 60, (210, 210, 210), -1)
    cv2.circle(image, (390, 300), 60, (225, 225, 225), -1)
    noise = rng.normal(0, 3, image.shape).astype(np.int16)
    return np.clip(image.astype(np.int16) + noise, 0, 255).astype(np.uint8)


def make_conveyor_video(path: Path, objects: int = 12) -> dict:
    """亮色圆从左向右依次通过 x=2/3 处的检测线（正向真值 = objects）。"""
    width, height, fps = 800, 400, 25
    writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"MJPG"), fps, (width, height))
    if not writer.isOpened():
        raise RuntimeError("VideoWriter 打开失败: %s" % path)
    rng = np.random.default_rng(123)
    duration_s = objects * 2.2
    radius = 26
    spacing = duration_s / objects
    y_base = height // 2
    for frame_idx in range(int(duration_s * fps)):
        image = np.full((height, width, 3), 100, dtype=np.uint8)
        t = frame_idx / fps
        for i in range(objects):
            start = i * spacing
            x = int((t - start) * 160)  # 160 px/s
            if -radius <= x <= width + radius:
                y = y_base + int(18 * np.sin(i * 1.7))
                shade = 185 + (i % 5) * 12
                cv2.circle(image, (x, y), radius, (shade, shade, shade), -1)
        noise = rng.normal(0, 2, image.shape).astype(np.int16)
        frame = np.clip(image.astype(np.int16) + noise, 0, 255).astype(np.uint8)
        writer.write(frame)
    writer.release()
    return {"file": str(path), "frames": int(duration_s * fps), "fps": fps, "objectsForward": objects}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    rng = np.random.default_rng(args.seed)
    OUT_STATIC.mkdir(parents=True, exist_ok=True)
    OUT_VIDEO.mkdir(parents=True, exist_ok=True)

    truth: dict[str, int] = {}
    counts = [0, 0, 1, 2, 3, 5, 5, 8, 10, 10, 12, 15, 20, 20, 25, 30]
    for idx, count in enumerate(counts):
        name = f"img_{idx:02d}_n{count:02d}.png"
        image, actual = make_image(count, rng)
        cv2.imencode(".png", image)[1].tofile(str(OUT_STATIC / name))
        truth[name] = actual

    overlap_name = "overlap_2.png"
    cv2.imencode(".png", make_overlap_image(rng))[1].tofile(str(OUT_STATIC / overlap_name))
    truth[overlap_name] = 2  # 真值按人工计 2 个

    (OUT_STATIC / "truth.json").write_text(
        json.dumps(truth, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    video_info = make_conveyor_video(OUT_VIDEO / "conveyor_forward.avi")

    manifest = {
        "seed": args.seed,
        "staticCount": {"dir": str(OUT_STATIC), "images": len(truth), "truth": truth},
        "conveyor": video_info,
    }
    (REPO / "samples" / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print("生成完成：静态图 %d 张，视频 %s" % (len(truth), video_info["file"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
