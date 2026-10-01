# VisionWorkbench

VisionWorkbench 是一个面向工业视觉检测的 Windows AI 视觉工作台，提供从图像采集、模型推理、规则判定、结果存储到历史分析的完整工作流。

项目适合用于学习工业视觉软件架构、开发算法插件、搭建离线图片检测流程，以及集成 TCP/IP 触发式检测系统。

## 主要功能

- 实时检测：支持图片目录、视频文件、USB 相机和工业相机等输入源。
- AI 任务：目标检测、语义分割、实例分割、行为识别、AI 文本识别、条码和二维码识别。
- 任务配置：任务参数、ROI、计数模式、规则判定、SOP 流程和结果回传模板。
- 算法插件：算法在独立 Worker 进程中运行，通过 UTF-8 JSON Lines 协议与主程序通信。
- TCP/IP 通讯：支持项目配置、客户端/服务端模式、任务触发、异步响应和结果回传。
- 并发执行：支持同一工位多个触发请求并行执行，并为每个请求保留独立的 requestId、状态和结果上下文。
- 数据持久化：使用 SQLite 保存任务、检测记录、批次、统计、审核和系统事件。
- 历史数据看板：支持任务、状态和时间范围筛选，展示检测量、良率、任务排名和耗时分位数。
- 许可证模块控制：可以按数据与模型模块限制任务配置、实时检测、TCP 执行和相关功能入口。

## 系统架构

```text
相机 / 图片目录 / 视频
          │
          ▼
WPF 实时检测界面 ── TCP/IP 触发
          │
          ▼
帧调度与检测运行时
          │
          ▼
算法 Worker（JSON Lines）
          │
          ▼
统一检测结果 → 规则判定 → SQLite / 历史看板 / TCP 响应
```

算法 Worker 与主程序进程隔离。Worker 崩溃、超时或异常退出时，主程序可以记录明确错误并执行恢复策略，不会直接破坏桌面应用进程。

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `src/VisionWorkbench.App` | WPF 桌面应用和主要用户界面 |
| `src/VisionWorkbench.Contracts` | 算法输出、插件清单和通信协议模型 |
| `src/VisionWorkbench.Domain` | 任务、规则、批次、统计和领域模型 |
| `src/VisionWorkbench.Application` | 检测运行时、帧调度、任务和应用服务 |
| `src/VisionWorkbench.Persistence` | Entity Framework Core SQLite 持久化实现 |
| `src/VisionWorkbench.Algorithms` | 算法会话、Worker 生命周期和插件管理 |
| `src/VisionWorkbench.Infrastructure` | 进程管理、日志、图像临时文件和基础设施 |
| `src/VisionWorkbench.Cameras.*` | 相机抽象、图片/视频、USB 和工业相机适配器 |
| `workers` | Python Worker、插件 SDK 和示例算法 |
| `tests/VisionWorkbench.Tests` | 协议、规则、调度、持久化和 TCP 测试 |
| `docs` | 设计说明和专题文档 |

## 环境要求

- Windows 10 或 Windows 11
- .NET 10 SDK
- Python 3.12（运行 Python 算法 Worker 时需要）
- Visual Studio 2022、Rider 或 VS Code 均可用于开发
- 工业相机功能需要对应厂商 SDK 和驱动；没有工业相机时可以使用图片目录或视频文件进行离线测试

## 快速开始

### 1. 获取代码

```powershell
git clone https://github.com/guanhaiye/VisionWorkBench.git
cd VisionWorkBench
```

### 2. 准备 Python Worker 环境（可选）

```powershell
py -3.12 -m venv workers\.venv
workers\.venv\Scripts\python.exe -m pip install -r workers\requirements.txt
```

如果只使用图片目录、视频文件和已构建的 .NET 组件，可以暂时跳过 Python 环境配置。

### 3. 构建和运行

```powershell
dotnet restore VisionWorkbench.slnx
dotnet build VisionWorkbench.slnx -c Release
dotnet run --project src\VisionWorkbench.App\VisionWorkbench.App.csproj
```

首次运行后，可以在任务配置中选择图片目录或视频文件作为输入源，避免在没有相机设备时无法验证流程。

### 4. 运行测试

```powershell
dotnet test tests\VisionWorkbench.Tests\VisionWorkbench.Tests.csproj -c Release
```

生成测试图片（可选）：

```powershell
workers\.venv\Scripts\python.exe tools\make_test_images.py
```

## TCP/IP 触发示例

TCP 项目配置完成并启动通讯后，可以发送任务触发指令。文本指令示例：

```text
START_ST-001
```

收到合法指令后，服务会先返回 `accepted`，检测完成后再返回带有相同 `requestId` 的最终结果。多个请求可以并行执行，最终结果允许乱序返回，但每个结果都通过 `requestId` 关联到原始请求。

JSON 请求也可以携带请求编号和任务信息，具体字段以应用内通信配置和协议模型为准。

默认并发参数如下，均可按 TCP 项目配置调整：

| 参数 | 默认值 |
| --- | ---: |
| 同工位最大并发数 | 3 |
| 等待队列长度 | 20 |
| 单次执行超时 | 30 秒 |

## 数据和模型

应用运行数据默认保存在 Windows 用户目录下的 `VisionWorkbench` 数据目录中，包括数据库、日志、临时文件和检测证据图。模型文件和大型数据集不会随 Git 仓库发布，建议使用项目内的模型管理或数据管理功能配置本地路径。

请勿将以下内容提交到公开仓库：

- 真实生产图片、检测证据和客户数据
- 许可证文件、设备指纹、私钥和访问令牌
- 工业相机厂商 SDK、商业模型和未获授权的第三方文件
- `workers/.venv`、本地数据库和运行日志

## 许可证模块

VisionWorkbench 支持按“数据与模型模块”发放许可证。许可证可以只开放目标检测，也可以同时开放语义分割、实例分割、行为识别等模块。

当许可证不包含某个模块时：

- 任务配置列表会保留任务，但标记为“已禁用”；
- 未授权任务不能在实时检测中选择或运行；
- TCP 触发未授权任务会被拦截；
- 对应的数据与模型导航入口会被禁用。

## 开发说明

建议在提交代码前运行：

```powershell
dotnet build VisionWorkbench.slnx -c Release
dotnet test tests\VisionWorkbench.Tests\VisionWorkbench.Tests.csproj -c Release
```

新增算法时，优先复用 `workers/python-sdk` 中的 Worker 协议和生命周期实现。新增相机时，优先实现 `VisionWorkbench.Cameras.Abstractions` 定义的接口，并补充离线或模拟输入测试。

## 文档

- [项目交接文档](HANDOFF.md)
- [商业化实施方案](VisionWorkbench-商用化完整实施方案.md)
- [Python Worker SDK](workers/python-sdk)
- [测试项目](tests/VisionWorkbench.Tests)

## 开源许可证

发布前请在仓库根目录补充 `LICENSE` 文件，并根据实际授权范围选择 MIT、Apache-2.0 或其他合适的开源许可证。第三方模型、相机 SDK 和插件的许可证不因本项目开源而自动改变，使用前请分别确认其授权条件。

## 项目链接

- GitHub：<https://github.com/guanhaiye/VisionWorkBench>
- Gitee：<https://gitee.com/yzbnb/vision-workbench>
