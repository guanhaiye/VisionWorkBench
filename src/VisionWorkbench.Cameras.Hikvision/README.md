# 海康工业相机 Provider

`VisionWorkbench.Cameras.Hikvision` 使用海康机器人 MVS 原生 C API 接入 GigE Vision 和 USB3 Vision 工业相机。

## 运行时要求

安装 MVS Runtime/SDK（x64），使 `MvCameraControl.dll` 位于 MVS 默认 Runtime 目录或通过 `VISIONWORKBENCH_MVS_PATH` 指定目录。项目不引用厂商托管程序集，也不将厂商 DLL 打入发布包；未安装 SDK 时 Provider 返回空设备列表并记录诊断日志。

## 任务配置

任务配置的输入源选择“海康工业相机”，刷新后按型号、IP、序列号选择设备。保存任务后，实时检测与 SOP 运行都通过统一 `ICameraSession` 接收 BGR 帧。设备绑定使用 `gige:<序列号>` 或 `usb3:<序列号>`，没有序列号时退回 IP/枚举索引。

## 参数

`CameraOpenOptions.Parameters` 支持宽度、高度、帧率、曝光、增益、自动曝光、自动增益、像素格式、触发模式和触发源；`HikvisionCameraSession.TriggerSoftwareAsync()` 用于软件触发。相机型号不支持的 GenICam 节点会被记录并继续使用设备默认值。
