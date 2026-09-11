# 硬件兼容性清单

Windows 10/11 x64。相机输入源包括离线图片、离线视频、标准 USB/DirectShow、RTSP/HTTP 网络流和海康机器人 MVS 工业相机。

## 海康工业相机

VisionWorkbench 通过独立的 MVS Provider 原生接入海康 GigE Vision 与 USB3 Vision 相机，支持：

- MVS SDK 枚举、序列号/型号/IP 显示和按稳定设备标识绑定任务；
- 独占打开、连续取流、BGR 帧转换、预览和实时检测；
- 宽度、高度、帧率、曝光、增益、自动曝光、自动增益、像素格式、触发模式和触发源参数；
- 软件触发接口、采集异常检测、设备断线后的自动重连（最多 10 次）；
- MVS 未安装时 Provider 安全降级，不影响软件启动。

使用前需在目标机器安装与系统位数匹配的海康 MVS Runtime/SDK，并确保 `MvCameraControl.dll` 可被发现。默认搜索路径为 MVS 的 `Common Files\MVS\Runtime\Win64_x64`；也可以通过环境变量 `VISIONWORKBENCH_MVS_PATH` 指向包含该 DLL 的目录。MVS SDK 属于海康机器人发行组件，发布包不内置或重新分发厂商 DLL。

当前未接入海康专用光源控制器、相机内置 FPGA 算法、Camera Link/CXP 采集卡和多相机硬件同步验收；这些设备可在现场通过 MVS/GigE/USB 取流，但硬件触发时序、PTP 和 IO 电气特性仍需按具体型号验收。

GPU/CUDA、串口、PLC/OPC UA 和厂商 SDK 必须在现场验收表中记录型号、驱动、固件、分辨率、帧率和断线恢复结果；未验证设备显示为未认证。
