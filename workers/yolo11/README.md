# YOLO11 插件

插件 ID：`com.vision.yolo11`

支持三种 `task`：

- `detect`：使用 `yolo11*.pt` 输出目标框，用于目标定位；
- `instance`：使用 `yolo11*-seg.pt` 输出每个实例独立的框和轮廓；
- `semantic`：优先读取模型的 `semantic_mask`；使用 YOLO11-seg 时按类别合并实例掩膜，输出语义区域轮廓。

模型权重不随仓库提交。将权重放到插件目录，例如：

```text
workers/yolo11/models/yolo11n.pt
workers/yolo11/models/yolo11n-seg.pt
```

然后在任务配方的算法设置中选择：

```json
{
  "task": "instance",
  "modelPath": "models/yolo11n-seg.pt",
  "confidence": 0.25,
  "iou": 0.7,
  "imageSize": 640,
  "device": "auto",
  "saveMasks": false
}
```

安装运行时：

```powershell
python -m pip install -r workers/requirements.txt
```

CPU/CUDA 的 PyTorch 版本应按现场环境单独选择。`device=auto` 会优先使用 CUDA；没有 CUDA 时回退 CPU。语义模式使用 YOLO11-seg 时属于“实例掩膜按类别合并”的语义投影，不等同于专用语义分割模型训练结果。
