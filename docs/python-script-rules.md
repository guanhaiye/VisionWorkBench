# Python 后处理脚本编写规则

任务配置中的 Python 后处理脚本用于对模型检测结果进行二次筛选。平台负责准备输入 JSON 和执行脚本，用户脚本只需要读取 `input_data`，并给 `result` 赋值。

## 固定接口

- 固定输入变量：`input_data`
- 固定输出变量：`result`
- 其他内部变量、`import` 和处理逻辑可以自由编写。

脚本不要读取标准输入、不要自行输出 JSON，也不要修改变量名。平台会自动完成这些工作。

## input_data

通用字段包括：`schemaVersion`、`taskType`、`imagePath`、`imageWidth`、`imageHeight`、`items` 和 `raw`。

`items` 是统一的当前结果对象列表，每个对象带有稳定的 `index`，脚本应使用这个 index 返回保留对象。

- 目标检测：`kind`、`index`、`classId`、`className`、`confidence`、`areaPx`、`diameterPx`、`box`
- 语义/实例分割：`kind=segmentation`、`index`、`mode`、`classId`、`confidence`、`areaPx`、`diameterPx`、`contours`
- 行为识别：`kind=behaviorEvent`、`index`、`eventId`、`eventType`、`phase`、`severity`、`confidence`、`durationMs`

面积和直径均为像素值，不是归一化值。行为识别事件没有面积字段，应使用事件类型、严重程度、置信度和持续时间等字段。

## result

`result` 必须是对象，并且只能使用下列字段：

```python
result = {
    "keepIndices": [0, 2],
    "status": "ok",
    "message": "保留 2 个对象"
}
```

- `keepIndices`：可选，填写 `items` 中对象的原始 `index` 数组。
- `status`：必填，只能是 `ok` 或 `ng`。
- `message`：可选，填写结果说明。

脚本执行失败、没有设置 `result`、缺少 `status`、status 不合法或输出了未定义字段时，本次结果会按 NG 处理并记录错误。

## 示例：面积 0~99999 且数量必须为 6

```python
items = input_data.get("items", [])
kept_indices = []

for item in items:
    area_px = item.get("areaPx", 0)
    try:
        area_px = float(area_px)
    except (TypeError, ValueError):
        continue

    if 0 <= area_px <= 99999:
        kept_indices.append(item.get("index"))

final_count = len(kept_indices)
result = {
    "keepIndices": kept_indices,
    "status": "ok" if final_count == 6 else "ng",
    "message": f"面积筛选后数量：{final_count}，期望数量：6"
}
```
