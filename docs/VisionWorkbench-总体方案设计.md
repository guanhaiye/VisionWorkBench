# VisionWorkbench Windows AI 视觉工作台总体方案设计

> 文档版本：1.0  
> 设计状态：总体方案确认稿  
> 目标平台：Windows 10/11 x64  
> 技术主线：C#/.NET 10/WPF 宿主 + Python/ONNX/C++ 独立算法 Worker

## 1. 文档目的

本文档定义一款 Windows 本地 AI 视觉工作台的完整产品与技术方案，作为后续原型、开发、测试和交付的统一依据。

系统面向固定相机和固定光源的现场视觉任务，首要覆盖：

- 静态物体计数；
- 动态目标去重计数；
- 流水线跨线、区域转移和触发计数；
- 包装有无与漏装检查；
- 表面缺陷识别；
- 区域入侵、停留超时和简单行为检测；
- 图片、视频、USB 相机和后续工业相机输入；
- CPU 保底运行、GPU 可选加速；
- Python、ONNX 和 C++ 算法持续扩展。

本产品默认在客户 Windows 电脑上离线运行，不依赖公有云。操作人员不直接接触复杂模型参数，而是选择已经配置好的“视觉配方”运行。

---

## 2. 产品定位与设计原则

### 2.1 产品定位

VisionWorkbench 是一个统一的 Windows AI 视觉宿主。它负责设备、流程、显示、规则、数据和算法插件生命周期，具体识别能力由可替换的算法 Worker 提供。

产品不是“万能识图工具”，而是一个可配置、可记录、可追溯的现场视觉执行平台。

### 2.2 核心设计原则

1. **整体产品、分层设计、分进程运行。**
2. **核心稳定，边缘可插拔，高风险进程隔离。**
3. **C# 宿主管理业务，算法 Worker 专注视觉推理。**
4. **模型输出事实，规则引擎完成业务判定。**
5. **CPU 是基础能力，GPU 是性能加速选项。**
6. **操作员零调参，工程师一次配置，专家维护底层参数。**
7. **累计数据由事件产生，不能只保存不可追溯的总数。**
8. **实时预览和算法推理解耦，算法慢不能拖死界面。**
9. **先建立完整纵向闭环，再增加算法类型和工业适配。**
10. **不过度插件化，不把编译期问题推迟到客户现场。**

### 2.3 首版不包含

- 公有云 SaaS；
- 手机 App；
- 多工厂集中管理；
- 复杂多用户权限；
- 在线模型训练；
- 自动标注平台；
- 多机分布式推理；
- 机械剔除闭环；
- 高速线阵、三维点云、高光谱和 X 光检测。

---

## 3. 最终架构决策

### 3.1 开发与交付形式

采用：

```text
一个产品
一个代码仓库
一个安装包
一个 Windows 主程序
零到多个算法 Worker 进程
一套版本化通信协议
多个可插拔算法包
```

不采用传统网页项目的“前端 + 远程服务器后端”结构。这里更准确的划分是：

- **Windows 宿主程序**：C# + WPF；
- **本地算法运行层**：Python、ONNX 或 C++ Worker。

两者独立运行、独立测试，但在同一个仓库中协同开发，并由同一个安装程序交付。

### 3.2 总体结构

```text
┌────────────────────────────────────────────────────────────┐
│              VisionWorkbench Windows 主程序               │
│                    C# + .NET 10 + WPF                     │
│                                                            │
│  UI：实时检测、任务配方、历史、算法、设备、系统设置       │
│  应用：任务状态、帧调度、规则、计数、记录、插件管理       │
│  设备：Camera Provider、相机会话、断线重连                │
│  数据：SQLite、图片、视频、日志、配置                      │
└──────────────────────────┬─────────────────────────────────┘
                           │
                 JSON 控制协议 + 图像传输
                           │
┌──────────────────────────▼─────────────────────────────────┐
│                       算法 Worker 层                       │
│                                                            │
│  Python Worker       ONNX Worker        C++ Worker         │
│  PyTorch/OpenCV      CPU/CUDA/OpenVINO  TensorRT/厂商 SDK  │
└────────────────────────────────────────────────────────────┘
```

### 3.3 为什么算法必须进程隔离

算法不直接嵌入 WPF 主进程，原因包括：

- Python、CUDA 和原生 DLL 依赖可能冲突；
- 不同算法可能要求不同运行环境；
- 模型加载和推理不能阻塞 UI；
- 算法异常、显存不足或进程崩溃不能拖垮主程序；
- 算法应能独立更新、重启和诊断；
- 后续可以用 ONNX 或 C++ Worker 替换 Python，而不修改界面。

---

## 4. 技术栈

| 层级 | 技术选型 |
|---|---|
| 操作系统 | Windows 10/11 x64 |
| 主程序语言 | C# |
| 运行时 | .NET 10 LTS |
| UI | WPF、XAML |
| UI 架构 | MVVM |
| MVVM 工具 | CommunityToolkit.Mvvm |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 配置 | Microsoft.Extensions.Configuration |
| 日志 | Serilog |
| 数据库 | SQLite + EF Core |
| USB/视频基础接入 | OpenCvSharp |
| 算法研发 | Python 3.12、PyTorch、OpenCV |
| 通用推理 | ONNX Runtime |
| CPU 优化 | ONNX Runtime CPU；后续 OpenVINO |
| GPU 加速 | CUDA/TensorRT；后续 DirectML |
| 算法控制通信 | JSON Lines + stdin/stdout |
| 首版图像传输 | 临时图片文件 |
| 实时图像传输 | 后续共享内存双缓冲/环形缓冲 |
| 测试 | xUnit + Python pytest + 集成测试 |
| 安装 | .NET 自包含发布 + Inno Setup |

### 4.1 为什么主程序选择 WPF

WPF 适合本产品的桌面工具属性：

- 框架成熟，MVVM 和数据绑定稳定；
- 适合实时画面、多面板和复杂桌面操作；
- 与厂商 .NET/C/C++ SDK 集成方便；
- 自包含交付路径直接；
- UI 不处于模型推理热路径，不会成为主要性能瓶颈。

当极端性能场景出现时，应把相机采集或推理热点替换成 C++ 模块，而不是重写整个 WPF 产品。

---

## 5. 软件模块划分

软件逻辑上由 10 个核心模块组成。

| 编号 | 模块 | 职责 |
|---:|---|---|
| 1 | 桌面界面模块 | 页面、实时画面、叠加图层和用户交互 |
| 2 | 任务与配方模块 | 组合相机、算法、区域、规则和保存策略 |
| 3 | 相机与设备模块 | 设备发现、连接、取流、参数和重连 |
| 4 | 图像与帧调度模块 | 图像转换、ROI、采样、队列和传输 |
| 5 | 算法插件管理模块 | 插件发现、安装、启动、监控和重启 |
| 6 | 算法 Worker 模块 | 模型加载、推理、跟踪和事件生成 |
| 7 | 规则与判定模块 | 将算法事实转换为 OK/NG/待确认 |
| 8 | 计数与事件模块 | 静态、动态、流水线计数和行为事件 |
| 9 | 数据与历史模块 | SQLite、图片证据、批次、纠错和查询 |
| 10 | 系统与诊断模块 | 配置、日志、性能测试、备份和诊断 |

运行时进程结构为：

```text
1 个 VisionWorkbench.exe 主进程
+
0～N 个算法 Worker 进程
```

---

## 6. 插件化边界

### 6.1 不采用“所有模块 DLL 插件化”

全部插件化会导致：

- 编译期错误推迟到客户现场；
- 模块依赖图复杂；
- 版本、卸载和原生依赖难以诊断；
- DLL 插件仍在同一进程，不能真正隔离崩溃；
- 开发与测试成本显著上升。

### 6.2 三类模块处理策略

#### 核心模块：静态编译

- WPF 界面；
- 任务与配方；
- 帧调度；
- 算法宿主管理；
- 规则与判定；
- 计数与事件；
- 数据与历史；
- 系统与诊断。

#### 边缘扩展：DLL Provider

- 工业相机 Provider；
- PLC/协议适配器；
- 数据导出器；
- 外部通知适配器。

#### 高风险扩展：独立进程

- Python 算法；
- CUDA/TensorRT 算法；
- 复杂 C++ 算法；
- 易冲突或稳定性未知的厂商原生 SDK。

最终原则：

```text
核心稳定
边缘可插拔
高风险进程隔离
```

### 6.3 模块失败分级

| 类型 | 示例 | 失败策略 |
|---|---|---|
| 致命核心 | 配置、任务、数据库基础、协议 | 进入安全故障页，禁止检测 |
| 可降级扩展 | 某相机、某算法、PLC、PDF 导出 | 禁用对应功能，其他功能继续 |
| 可替代必需项 | 配方指定的相机或算法不可用 | 软件可启动，但该任务不能运行 |

---

## 7. 代码仓库与工程结构

```text
VisionWorkbench/
├── src/
│   ├── VisionWorkbench.App/
│   ├── VisionWorkbench.Application/
│   ├── VisionWorkbench.Domain/
│   ├── VisionWorkbench.Contracts/
│   ├── VisionWorkbench.Infrastructure/
│   ├── VisionWorkbench.Persistence/
│   ├── VisionWorkbench.Cameras.Abstractions/
│   ├── VisionWorkbench.Cameras.Usb/
│   ├── VisionWorkbench.Cameras.Files/
│   └── VisionWorkbench.Algorithms/
├── workers/
│   ├── python-sdk/
│   ├── sample-counter/
│   ├── sample-defect/
│   └── onnx-worker/
├── camera-providers/
├── plugins/
├── contracts/
├── samples/
├── tests/
├── installer/
└── docs/
```

Visual Studio 解决方案建议：

```text
VisionWorkbench.sln
├── VisionWorkbench.App
├── VisionWorkbench.Application
├── VisionWorkbench.Domain
├── VisionWorkbench.Contracts
├── VisionWorkbench.Infrastructure
├── VisionWorkbench.Persistence
├── VisionWorkbench.Cameras.Abstractions
├── VisionWorkbench.Cameras.Usb
├── VisionWorkbench.Cameras.Files
├── VisionWorkbench.Algorithms
└── VisionWorkbench.Tests
```

Python 代码位于同一仓库，但不作为 .NET 工程编译。

---

## 8. 前端界面设计

### 8.1 导航结构

```text
实时检测
任务配置
历史记录
算法管理
设备管理
系统设置
```

### 8.2 主工作台

```text
┌─────────────────────────────────────────────────────────────┐
│ AI视觉工作台  配方：A型产品  相机：Camera 01  ●运行中      │
├─────────┬──────────────────────────────────┬────────────────┤
│ 实时检测│                                  │ 当前结果       │
│ 任务配置│                                  │                │
│ 历史记录│          实时相机画面            │    1,286       │
│ 算法管理│                                  │                │
│ 设备管理│   检测框、ROI、检测线、轨迹      │  ✓ 正常        │
│ 系统设置│                                  │                │
│         │                                  │ 65 件/分钟     │
│         │                                  │ 推理 38ms      │
├─────────┴──────────────────────────────────┴────────────────┤
│ [开始] [暂停] [单次检测] [结束批次] [人工修正]             │
├─────────────────────────────────────────────────────────────┤
│ 相机30FPS | 算法12FPS | CPU/CUDA | 数据库正常              │
└─────────────────────────────────────────────────────────────┘
```

### 8.3 图像叠加层

- 原始画面层；
- ROI 区域层；
- 检测线与方向层；
- 检测框层；
- 分割掩膜层；
- 关键点层；
- 轨迹层；
- 事件提示层；
- 文字标签层。

图像背景使用 `WriteableBitmap` 或后续 `D3DImage`，检测框使用独立绘制层，避免把标注反复绘制进原图。

### 8.4 UI 刷新策略

```text
相机预览：25～30 FPS
检测图层：算法结果到达时更新
统计数字：5～10 Hz
历史列表：按事件或批次更新
```

UI 线程不得执行模型推理、图片编码或数据库写入。

### 8.5 状态设计

```text
未配置
→ 设备就绪
→ 算法加载中
→ 等待检测
→ 检测中
   ├── OK
   ├── NG
   ├── 待确认
   └── 故障
```

状态必须同时使用颜色、图标和文字。

---

## 9. 参数、角色与视觉配方

### 9.1 配置原则

底层必须具备完整参数系统，但不把复杂参数暴露给现场人员。

### 9.2 三级使用模式

#### 操作员

可操作：

- 选择视觉配方；
- 输入批次号；
- 开始、暂停、结束；
- 确认报警；
- 人工修正计数。

#### 工程师

可配置：

- 相机；
- ROI；
- 检测线和方向；
- 目标类别；
- 标准数量；
- 检测灵敏度；
- 保存策略；
- 试运行和校准。

#### 专家

可配置：

- 模型路径和输入尺寸；
- 置信度、IoU 和 NMS；
- 跟踪器参数；
- CPU/GPU 后端；
- 推理 FPS；
- Worker 和协议调试参数。

### 9.3 视觉配方

现场人员使用配方，而不是裸模型。

一份配方包含：

```text
产品/任务名称
相机及相机参数
算法插件和模型版本
计数或检测模式
ROI、检测线和方向
业务规则
算法参数预设
触发方式
保存策略
```

典型流程：

```text
导入算法包
→ 工程师选择相机并配置现场
→ 运行测试
→ 保存视觉配方
→ 操作员选择配方直接运行
```

### 9.4 项目与多工位

实际项目由一个或多个工位组成，不再把“一个项目”等同于“一个检测任务”。配置层级统一为：

```text
项目（Project）
├─ 工位 ST-001（Station）→ 相机/输入源 → 视觉配方 → 检测结果
├─ 工位 ST-002（Station）→ 相机/输入源 → 视觉配方 → 检测结果
└─ 工位 ST-003（Station）→ 相机/输入源 → 视觉配方 → 检测结果
```

每个工位至少包含：

| 字段 | 说明 |
|---|---|
| `StationId` | 数据库内部主键，不对外作为业务编号 |
| `StationCode` | 项目内唯一、稳定的工位编号，例如 `ST-001` |
| `Name` | 工位显示名称 |
| `Enabled` | 是否允许启动检测 |
| `TaskId/RecipeId` | 当前绑定的任务或视觉配方 |
| `CameraProviderId/CameraDeviceId` | 独立输入设备绑定 |

工位管理规则：

- 工程师可以在项目中增加、编辑、启用、停用和删除工位；
- `StationCode` 必填，去除首尾空格后长度不超过 64 个字符，比较时不区分大小写；
- 同一项目内编号必须唯一，不同项目允许复用；编号建立后不因工位改名而改变；
- 新建工位时可建议 `ST-001`、`ST-002` 等可用编号，但保存前仍由后端校验唯一性；
- 运行中的工位不得直接删除；应先停止检测并结束批次；
- 已产生记录的工位删除采用逻辑删除/归档，历史结果和编号快照必须保留；
- 每个工位独立维护运行状态、相机会话、算法会话、批次和计数状态，一个工位故障不得停止其他工位；
- 同一物理相机或独占设备原则上只能绑定一个运行中的工位，冲突时禁止启动并明确提示。

实时工作台按工位展示状态，可单独启动/停止，也可执行项目级“全部启动/全部停止”。项目级操作必须逐工位返回成功或失败明细，不得因部分失败把全部工位显示为成功。

---

## 10. 相机与设备模块

### 10.1 设计模式

采用：

```text
抽象工厂 ICameraProvider
+ Provider 注册表
+ CameraManager
+ ICameraSession
```

### 10.2 结构

```text
CameraManager
    ↓
CameraProviderRegistry
    ├── UsbCameraProvider
    ├── ImageFolderProvider
    ├── VideoFileProvider
    ├── HikvisionProvider
    ├── DahengProvider
    └── BaslerProvider
         ↓
    ICameraSession
         ↓
      VideoFrame
```

### 10.3 Provider 接口

```csharp
public interface ICameraProvider
{
    string ProviderId { get; }
    string DisplayName { get; }

    Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken);

    Task<ICameraSession> CreateSessionAsync(
        CameraDescriptor descriptor,
        CancellationToken cancellationToken);
}
```

### 10.4 会话接口

```csharp
public interface ICameraSession : IAsyncDisposable
{
    CameraDescriptor Descriptor { get; }
    CameraSessionState State { get; }
    CameraCapabilities Capabilities { get; }

    Task OpenAsync(
        CameraOpenOptions options,
        CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    Task ApplyParametersAsync(
        CameraParameterSet parameters,
        CancellationToken cancellationToken);

    event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    event EventHandler<CameraFaultedEventArgs>? Faulted;
}
```

### 10.5 首版与后续输入源

首版：

- USB 摄像头；
- 单张图片；
- 图片目录；
- 视频文件。

后续：

- 海康、大恒、Basler；
- RTSP；
- GigE Vision/GenICam；
- 软件和硬件触发。

### 10.6 断线重连

```text
检测断线
→ 停止帧调度
→ 释放旧会话
→ 按序列号重新发现设备
→ 1/2/5/10/30 秒递增重试
→ 恢复相机参数
→ 恢复任务
```

---

## 11. 图像与帧调度

### 11.1 预览和推理解耦

```text
相机 30 FPS
├── UI 预览 30 FPS
└── 算法推理 5～15 FPS
```

### 11.2 队列策略

```csharp
public enum FrameQueuePolicy
{
    LatestOnly,
    BoundedQueue,
    EveryFrame,
    Sampled
}
```

- 普通实时检测：`LatestOnly`；
- 行为检测：`BoundedQueue`；
- 离线视频分析：`EveryFrame` 或 `Sampled`；
- 单次拍照：触发式提交。

### 11.3 图像传输

第一版：

```text
C# 抓图
→ 保存临时 JPEG/PNG
→ JSON 传文件路径
→ Python 读取并推理
```

实时优化版：

```text
C# 写共享内存
→ JSON 传共享内存名称和元数据
→ Python 使用 NumPy 视图读取
→ 返回结构化结果
```

控制消息使用 JSON，禁止将整张原始图像 Base64 塞入 JSON。

---

## 12. 算法插件包

### 12.1 交付单位

正式导入单位不是裸 `.pt` 或 `.onnx`，而是完整算法包：

```text
ObjectCounter.vwpkg
```

内部：

```text
object-counter/
├── plugin.json
├── settings.schema.json
├── worker.py 或 worker.exe
├── runtime/
├── models/
├── presets/
├── tests/
└── README.md
```

### 12.2 插件清单

```json
{
  "manifestVersion": "1.0",
  "id": "com.vision.object-counter",
  "name": "通用物体计数",
  "version": "1.0.0",
  "protocolVersion": "1.0",
  "minimumHostVersion": "1.0.0",
  "runtime": {
    "type": "python",
    "executable": "runtime/python.exe",
    "entry": "worker.py"
  },
  "capabilities": {
    "inputModes": ["single-image", "frame-stream"],
    "outputs": ["detection", "tracking", "metrics", "events"],
    "stateful": true,
    "realtime": true
  },
  "streaming": {
    "preferredFps": 10,
    "queuePolicy": "latest",
    "maximumQueueLength": 3
  },
  "executionProviders": ["cpu", "cuda", "openvino"],
  "settingsSchema": "settings.schema.json"
}
```

### 12.3 安装流程

```text
选择算法包
→ 校验签名/哈希
→ 检查协议和宿主版本
→ 检查 CPU/GPU 与运行环境
→ 解压到插件目录
→ 启动 Worker
→ 运行内置测试图片
→ 安装完成或回滚
```

---

## 13. 算法通信协议

### 13.1 控制通道

第一版使用标准输入输出 JSON Lines：

- 一行一条完整 JSON 消息；
- 主程序向 Worker 的 stdin 写请求；
- Worker 从 stdout 返回协议结果；
- 普通日志只能写 stderr；
- 每个请求必须有 `messageId`；
- 响应必须携带 `correlationId`；
- 模型只在 Worker 启动时加载一次。

### 13.2 基础消息

```json
{
  "protocolVersion": "1.0",
  "type": "submit",
  "messageId": "msg-001",
  "correlationId": null,
  "timestamp": "2026-08-16T10:30:00Z",
  "payload": {}
}
```

### 13.3 消息类型

```text
hello
initialize
ready
start_session
submit
result
event
update_settings
counter_command
health
cancel
flush
stop_session
shutdown
error
```

### 13.4 Worker 生命周期

```text
发现插件
→ 校验清单
→ 启动 Worker
→ 等待 hello
→ 发送 initialize
→ Worker 加载模型
→ 返回 ready
→ 创建算法会话
→ 提交图片/帧/信号
→ 接收结果和事件
→ flush
→ stop_session
→ shutdown
```

---

## 14. 统一算法输入输出

### 14.1 输入模式

- `FrameInput`：单帧或实时视频帧；
- `ImageSetInput`：前后对比、多角度图片；
- `VideoClipInput`：视频片段；
- `SignalInput`：传感器、按钮或 PLC 信号；
- `ControlInput`：重置、暂停、恢复和批次命令。

### 14.2 输出原语

- Classification：分类；
- Detection：目标框；
- Segmentation：掩膜或轮廓；
- Keypoints：关键点；
- Tracking：Track ID 与轨迹；
- Metrics：计数、面积、速度等指标；
- CountingEvent：计数事件；
- VisionEvent：行为或异常事件；
- Decision：算法建议判定；
- Performance：耗时、设备、帧率。

### 14.3 标准结果

```csharp
public sealed record AlgorithmOutput
{
    public required string OutputId { get; init; }
    public required string InputId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required long Sequence { get; init; }

    public IReadOnlyList<ClassificationResult> Classifications { get; init; } = [];
    public IReadOnlyList<DetectionResult> Detections { get; init; } = [];
    public IReadOnlyList<SegmentationResult> Segmentations { get; init; } = [];
    public IReadOnlyList<KeypointResult> Keypoints { get; init; } = [];
    public IReadOnlyList<TrackResult> Tracks { get; init; } = [];
    public IReadOnlyList<MetricResult> Metrics { get; init; } = [];
    public IReadOnlyList<CountingEvent> CountingEvents { get; init; } = [];
    public IReadOnlyList<VisionEvent> Events { get; init; } = [];

    public DecisionResult? Decision { get; init; }
    public PerformanceInfo? Performance { get; init; }
}
```

### 14.4 对外结果与工位标识

Worker 的算法输出保持算法无关，不要求算法识别工位；主程序在接收算法结果后添加工位上下文，形成对外发布和持久化使用的结果信封：

```json
{
  "schemaVersion": "1.0",
  "projectId": "project-a",
  "stationCode": "ST-002",
  "taskId": 18,
  "batchId": 203,
  "recordId": 9821,
  "timestamp": "2026-08-19T15:30:01.125Z",
  "decision": { "status": "ok", "summary": "数量符合" },
  "output": { "metrics": [{ "name": "count", "value": 5 }] }
}
```

`stationCode` 是所有对外结果的必填字段，包括实时事件、PLC/消息协议适配器、Webhook/HTTP、CSV 导出和诊断日志。外部设备应使用 `projectId + stationCode` 识别结果来源，不使用可变的工位名称或数据库自增 ID。

工位编号在检测开始时写入运行上下文，在结果落库和发布时再次校验非空。检测期间修改工位配置不影响当前批次；新配置从下一次启动生效。若无法确定工位编号，结果不得作为正常业务结果发布，应标记为配置错误并告警。

坐标统一使用 `0～1` 归一化坐标，避免算法依赖窗口尺寸。

---

## 15. 算法会话

静态算法可以无状态运行；动态计数、跟踪和行为检测必须维护会话状态。

```csharp
public interface IAlgorithmSession : IAsyncDisposable
{
    string SessionId { get; }
    AlgorithmSessionState State { get; }

    Task InitializeAsync(
        AlgorithmInitialization initialization,
        CancellationToken cancellationToken);

    Task StartAsync(
        AlgorithmStartOptions options,
        CancellationToken cancellationToken);

    Task SubmitAsync(
        AlgorithmInput input,
        CancellationToken cancellationToken);

    Task FlushAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    event EventHandler<AlgorithmOutputEventArgs>? OutputReceived;
    event EventHandler<AlgorithmEventEventArgs>? EventReceived;
    event EventHandler<AlgorithmFaultedEventArgs>? Faulted;
}
```

---

## 16. 计数系统

### 16.1 计数模式

```csharp
public enum CountingMode
{
    Snapshot,
    UniqueTracking,
    LineCrossing,
    ZoneTransition,
    Triggered
}
```

### 16.2 静态计数

```text
单帧
→ 图像质量检查
→ ROI 裁剪
→ 检测或实例分割
→ 置信度与 NMS 过滤
→ 边界规则
→ 按类别统计
```

输出当前画面数量，不保存跨帧状态。

### 16.3 动态去重计数

```text
连续视频
→ 目标检测
→ 多目标跟踪
→ 确认 Track ID
→ 历史 ID 去重
→ 新目标产生 Appeared 事件
→ 会话累计
```

必须区分：

- 当前可见数量；
- 稳定跟踪数量；
- 会话累计数量。

### 16.4 流水线跨线计数

```text
检测
→ 跟踪
→ 判断轨迹位于检测线哪一侧
→ 穿过滞回区
→ 确认完整通过
→ 产生 CrossedLine 事件
→ 正向/反向累计
```

检测线附近必须设置滞回区，避免目标框抖动导致重复计数。

### 16.5 区域转移计数

```text
进入 A 区
→ 到达确认 B 区
→ 离开到 C 区
→ 完成 A-B-C 后计数
```

适合跨线判断不稳定、必须确认产品完整通过的场景。

### 16.6 触发计数

```text
传感器/按钮/PLC 触发
→ 相机拍摄
→ 算法识别和质量判定
→ 生成计数事件
```

首版预留 `SignalInput`，后续增加串口、Modbus TCP、OPC UA 或数字 IO。

### 16.7 计数状态

```csharp
public sealed record CounterState
{
    public required string CounterId { get; init; }
    public required CountingMode Mode { get; init; }
    public long CurrentVisible { get; init; }
    public long SessionTotal { get; init; }
    public long ForwardTotal { get; init; }
    public long ReverseTotal { get; init; }
    public long AcceptedTotal { get; init; }
    public long RejectedTotal { get; init; }
    public long UncertainTotal { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
```

### 16.8 计数事件

```csharp
public sealed record CountingEvent
{
    public required string EventId { get; init; }
    public required string CounterId { get; init; }
    public string? TrackId { get; init; }
    public string? ClassId { get; init; }
    public required CountingEventType Type { get; init; }
    public string? Direction { get; init; }
    public long Delta { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public long FrameSequence { get; init; }
    public string? EvidenceImagePath { get; init; }
}
```

事件包括：

```text
Appeared
EnteredZone
ExitedZone
CrossedLine
Accepted
Rejected
Uncertain
Corrected
CounterReset
```

手动加一、减一、清零必须记录修改前后值、原因、时间和操作者。

---

## 17. 缺陷与行为算法

### 17.1 缺陷识别

标准输出包括：

- 缺陷框或多边形；
- 掩膜；
- 缺陷类别；
- 置信度；
- 严重等级；
- 面积、长度、占比等属性。

算法识别缺陷事实，规则引擎根据客户标准判断是否 NG。

### 17.2 行为检测

典型流水线：

```text
人员/物体检测
→ 目标跟踪
→ 姿态关键点（可选）
→ 区域关系
→ 时序状态机
→ VisionEvent
```

首批行为能力建议限定为：

- 进入/离开指定区域；
- 区域停留超时；
- 工具离开工具区；
- 工具未按时归还；
- 物体从 A 区移动到 B 区；
- 人员数量超限。

避免首版处理“操作是否规范”这类难以客观定义的行为。

事件阶段：

```text
Started
Updated
Completed
Cancelled
```

---

## 18. 规则与判定

算法输出事实，C# 规则引擎完成业务判断。

支持规则：

- 数量等于、大于或小于；
- 某类别必须存在；
- 某类别禁止出现；
- 指定区域必须有目标；
- 指定区域禁止有目标；
- 缺陷严重度或面积超过阈值；
- 事件持续时间超过阈值；
- 正向、反向或合格率异常；
- 置信度过低转人工确认。

统一判定：

```csharp
public enum DecisionStatus
{
    Ok,
    Ng,
    ReviewRequired,
    Unknown,
    Error
}
```

模型不确定时应输出 `ReviewRequired`，不得强迫模型每次给出肯定答案。

---

## 19. CPU/GPU 策略

### 19.1 总体原则

产品必须支持 CPU 运行，独立显卡不作为所有客户的强制要求。

```text
CPU 满足现场节拍
→ 不配置独立显卡

CPU 不足但核显可用
→ 尝试 OpenVINO/DirectML

多相机、高速、复杂模型
→ 配置 NVIDIA GPU
```

### 19.2 适合 CPU 的任务

- 单图分类；
- 静态计数；
- 有无和漏装；
- OCR；
- 低频缺陷检测；
- 单相机低速流水线；
- 轻量模型 5～10 FPS。

### 19.3 建议 GPU 的任务

- 多相机；
- 15～30 FPS 以上推理；
- 高分辨率细微缺陷；
- 大型分割；
- 多人姿态和复杂行为；
- 大型模型或高吞吐。

### 19.4 推理设备偏好

```csharp
public enum InferenceDevicePreference
{
    Auto,
    Cpu,
    IntegratedGpu,
    NvidiaGpu,
    Npu
}
```

默认 `Auto`，按插件支持和基准测试结果选择。

### 19.5 内置基准测试

每个任务建立后执行真实场景测试：

- 平均推理时间；
- P95 推理时间；
- 实际算法 FPS；
- CPU/GPU 占用；
- 内存和显存；
- 丢弃帧率；
- 是否达到任务要求。

系统向客户显示“是否满足节拍”，而不是要求客户理解 CUDA 参数。

### 19.6 CPU 优化顺序

```text
选择轻量模型
→ ROI 裁剪和降低模型输入尺寸
→ 降低算法 FPS
→ ONNX Runtime CPU
→ FP16/INT8 量化并复测精度
→ OpenVINO
→ 仍不满足时增加 GPU
```

---

## 20. 数据与历史

### 20.1 数据库表

#### Projects

```text
Id, ProjectCode, Name, Description
CreatedAt, UpdatedAt
```

#### Stations

```text
Id, ProjectId, StationCode, Name, Enabled, IsArchived
TaskId, CameraProviderId, CameraDeviceId
CreatedAt, UpdatedAt, ArchivedAt
Unique(ProjectId, StationCode)
```

#### Tasks

```text
Id, Name, Description
CameraProviderId, CameraDeviceId
PluginId, PluginVersion, ModelId
SettingsJson, RulesJson, RegionsJson
TriggerJson, StoragePolicyJson
CreatedAt, UpdatedAt
```

#### Batches

```text
Id, ProjectId, StationId, StationCode, TaskId, BatchNumber
StartedAt, EndedAt, Status
InitialCounterValue, FinalCounterValue
```

#### InspectionRecords

```text
Id, ProjectId, StationId, StationCode, TaskId, BatchId
StartedAt, CompletedAt, Status
OriginalImagePath, AnnotatedImagePath
AlgorithmElapsedMs, TotalElapsedMs
PluginVersion, ModelVersion
RawResultJson, FinalResultJson
WasCorrected
```

其中 `InspectionRecords.StationCode` 是结果产生时的编号快照，不只依赖 `StationId` 外键。工位改号或归档后，历史记录、CSV 和外部重放仍能还原当时的来源。`Batches`、`CountingEvents` 和 `VisionEvents` 可通过记录/批次关联工位；需要独立查询性能时增加 `StationId` 索引。

#### CountingEvents

```text
Id, RecordId, BatchId, CounterId
TrackId, ClassId, EventType, Direction
Delta, Confidence, OccurredAt
FrameSequence, EvidenceImagePath
```

#### VisionEvents

```text
Id, RecordId, BatchId
EventType, Phase, Severity
SubjectId, RegionId
StartedAt, EndedAt, Confidence
EvidenceImagePath
```

#### Corrections

```text
Id, RecordId
BeforeJson, AfterJson
Reason, OperatorName, CorrectedAt
```

### 20.2 保存原则

- 不把原图二进制直接写入 SQLite；
- SQLite 保存路径和结构化结果；
- 图片、视频和缩略图保存到数据目录；
- 原始模型输出与人工修正分开保存；
- 重新运行算法产生新的运行记录，不能覆盖旧结果。

### 20.3 默认保存策略

```text
OK：保存结构化数据，不默认保存原图
NG：保存原图和标注图
待确认：保存原图和标注图
计数：按需保存通过瞬间证据
行为报警：保存事件前后视频片段
```

磁盘不足时，停止保存图片并报警，但尽量维持检测和结构化记录。

---

## 21. 文件目录

安装目录与客户数据目录必须分离。

```text
安装目录/
├── VisionWorkbench.exe
├── 固定依赖 DLL
├── plugins/
├── camera-providers/
├── runtimes/
└── resources/

数据目录/
├── database/
├── images/
│   ├── original/
│   ├── annotated/
│   ├── events/
│   └── thumbnails/
├── videos/
├── exports/
├── backups/
├── logs/
├── config/
└── temp/
```

软件升级和卸载不得默认删除客户数据。

---

## 22. 异常处理与恢复

### 22.1 相机异常

标准错误：

```text
DEVICE_NOT_FOUND
DEVICE_BUSY
OPEN_FAILED
STREAM_START_FAILED
FRAME_TIMEOUT
DEVICE_DISCONNECTED
INVALID_PARAMETER
SDK_NOT_INSTALLED
SDK_VERSION_MISMATCH
```

### 22.2 算法异常

标准错误：

```text
PLUGIN_INVALID
PROTOCOL_NOT_SUPPORTED
RUNTIME_NOT_FOUND
MODEL_NOT_FOUND
MODEL_LOAD_FAILED
INPUT_INVALID
IMAGE_DECODE_FAILED
GPU_NOT_AVAILABLE
GPU_OUT_OF_MEMORY
INFERENCE_TIMEOUT
INFERENCE_FAILED
WORKER_TERMINATED
SETTINGS_INVALID
```

Worker 故障流程：

```text
暂停提交新帧
→ 保持 UI 和相机可用
→ 记录错误和 stderr
→ 尝试重启 Worker
→ 重新初始化模型和会话
→ 达到最大次数后等待人工处理
```

### 22.3 重启与计数恢复

计数总值由主程序根据已持久化事件恢复。目标跟踪状态通常无法跨重启完整恢复，因此流水线重启时应：

- 暂停生产线，或；
- 清空检测区，或；
- 由操作员确认当前状态。

---

## 23. 日志与诊断

日志文件：

```text
application.log
camera.log
algorithm-manager.log
plugin-{id}.log
database.log
performance.log
```

诊断包包括：

- 软件和协议版本；
- Windows、CPU、GPU 信息；
- 相机和插件列表；
- 当前配方的脱敏配置；
- 最近日志和错误；
- 可选问题图片。

客户原图默认不自动加入诊断包。

---

## 24. 安全与交付

- 客户不需要自行安装 Python；
- Python 运行时随插件或产品交付；
- 插件安装时校验哈希，后续增加数字签名；
- 算法进程以普通用户权限运行；
- API 密钥和许可证不得明文显示；
- 客户数据默认仅保存在本机；
- 插件协议必须有版本兼容检查；
- 卸载软件时默认保留数据。

安装包建议拆分：

```text
VisionWorkbench 主程序
基础 CPU 算法包
NVIDIA CUDA 算法包（可选）
工业相机 Provider（按需）
示例模型和配方
```

---

## 25. 性能目标

| 指标 | 首版目标 |
|---|---:|
| UI 预览 | 25 FPS 以上 |
| 轻量算法单次推理 | 目标 200ms 以内 |
| 实时算法 | 5～15 FPS |
| 单次检测响应 | 1 秒以内 |
| Worker 启动 | 10 秒以内 |
| 模型加载 | 30 秒以内 |
| 连续稳定运行 | 至少 8 小时 |
| 内存 | 无持续不可回收增长 |
| 数据保存 | 不阻塞实时检测 |

具体性能以真实模型、相机、分辨率和现场节拍基准测试为准。

---

## 26. 测试方案

### 26.1 单元测试

- 任务和配方校验；
- 规则引擎；
- 坐标转换；
- 计数事件累计；
- 插件清单解析；
- 协议序列化；
- 数据保存策略。

### 26.2 相机测试

- 启动、停止和重复连接；
- 断线重连；
- 帧队列溢出；
- 参数恢复；
- 软件退出时资源释放；
- 视频/图片虚拟相机回放。

### 26.3 静态计数测试

- 数量变化；
- 重叠和遮挡；
- 边缘目标；
- 反光与阴影；
- 不同背景；
- 人工纠正。

### 26.4 动态计数测试

- 同一轨迹不重复累计；
- 短暂遮挡；
- 两个目标交叉；
- 目标静止；
- 目标离开并返回；
- 算法掉帧。

### 26.5 流水线测试

- 正向一次只加一次；
- 反向单独统计；
- 检测线附近往返不重复；
- 两个目标并排；
- 产品粘连；
- 高速拖影；
- 中途返回不计；
- 暂停后停止累计；
- 重启后恢复批次总值。

### 26.6 故障测试

- Python 崩溃；
- 模型缺失；
- 显存不足；
- 相机断开；
- 数据库锁定；
- 磁盘满；
- 插件版本不兼容；
- 主程序异常退出后恢复。

---

## 27. 开发方式

采用纵向闭环，不采用“先做完全部前端、再做完全部后端、最后联调”。

### 27.1 第一闭环：架构验证

```text
USB 相机
→ WPF 实时预览
→ 启动 Python Worker
→ 提交一张图片
→ 返回检测框和数量
→ WPF 显示结果
```

### 27.2 第二闭环：静态计数产品化

```text
创建配方
→ 配置 ROI 和标准数量
→ 单次检测
→ OK/NG
→ 保存记录和图片
```

### 27.3 第三闭环：动态与流水线计数

```text
连续帧
→ 跟踪
→ 动态累计
→ 检测线
→ 正反向事件
→ 批次统计
```

### 27.4 第四闭环：插件扩展

```text
导入新算法包
→ 自动发现能力和参数
→ 不修改主程序
→ 正常运行并记录结果
```

---

## 28. 开发阶段与里程碑

### 阶段一：基础工作台

- WPF 框架和主题；
- 主导航；
- USB/图片/视频输入；
- 实时预览；
- SQLite 和日志。

### 阶段二：Python 插件闭环

- 插件扫描；
- Worker 生命周期；
- JSON Lines 协议；
- 文件传图；
- 静态计数示例；
- 检测框和数量显示；
- Worker 崩溃恢复。

### 阶段三：任务、配方与记录

- 配方编辑；
- ROI 编辑；
- 动态参数表单；
- 规则引擎；
- OK/NG；
- 历史、图片和人工纠错。

### 阶段四：动态和流水线计数

- 有界帧调度；
- 目标跟踪；
- 动态累计；
- 检测线和方向；
- 区域转移；
- 批次和计数事件；
- 手动加减与审计。

### 阶段五：缺陷与行为扩展

- 分割和关键点图层；
- 缺陷属性；
- 跟踪轨迹；
- 行为事件和时间线；
- 报警证据。

### 阶段六：性能与交付

- 共享内存；
- CPU/GPU 基准测试；
- OpenVINO/CUDA 后端；
- 工业相机 Provider；
- 稳定性和故障测试；
- 安装包、诊断包和备份。

---

## 29. MVP 范围

第一版必须实现：

1. WPF 统一界面；
2. USB 摄像头、图片和视频输入；
3. Python Worker 动态算法插件；
4. 静态物体计数；
5. 动态去重计数；
6. 流水线跨线计数；
7. ROI 和检测线编辑；
8. 当前、累计、正向和反向数量；
9. OK/NG 规则；
10. 任务和视觉配方；
11. SQLite 历史记录；
12. NG 和待确认图片证据；
13. 人工修正和审计；
14. CPU 推理；
15. CUDA 可选加速；
16. 相机和算法故障提示。

缺陷和行为输出协议应在首版定义，但完整算法可后续交付。

---

## 30. MVP 验收标准

### 基础

- 主程序能发现、启动和停止 Python 插件；
- 新算法目录无需重新编译 WPF 即可被发现；
- 算法崩溃不导致主程序退出；
- 主程序退出后不残留 Worker；
- 配方和设置重启后保留；
- 相机断线能够提示并尝试恢复。

### 多工位

- 一个项目可以增加、编辑、停用、归档多个工位；
- 同一项目内工位编号唯一，重复或空编号不能保存；
- 各工位可以绑定独立相机、配方、批次和计数状态；
- 多个工位并行运行时结果不串位，一个工位故障不影响其他工位；
- 所有落库、导出和对外发布结果均包含正确的 `projectId` 和 `stationCode`；
- 工位改名、改号或归档后，历史结果仍保留检测发生时的工位编号；
- 旧版单工位数据升级后自动生成默认项目和唯一工位编号，原有记录可查询。

### 静态计数

- 支持单次拍照计数；
- 显示检测框、类别和置信度；
- 支持 ROI 和边界规则；
- 支持标准数量和 OK/NG；
- 支持人工修正。

### 动态计数

- 同一轨迹不会每帧重复累计；
- 短暂遮挡后尽量保持 Track ID；
- 显示当前可见数和累计数；
- 支持开始、暂停和结束会话。

### 流水线计数

- 正向完整通过只增加一次；
- 反向通过单独统计；
- 检测线附近抖动不重复计数；
- 事件写入数据库；
- 重启后恢复批次累计值；
- 手动修正必须记录原因。

### 性能和稳定性

- UI 保持响应；
- 帧队列不无限增长；
- 数据写入不阻塞检测；
- 连续运行至少 8 小时；
- CPU 模式完成基础任务；
- GPU 模式能按插件能力启用和回退。

---

## 31. 最终结论

本项目采用以下最终方案：

```text
主程序：
C# + .NET 10 + WPF + MVVM

核心模块：
静态编译、统一测试、整体发布

相机扩展：
Provider 抽象工厂，工业厂商可使用 DLL 插件

算法扩展：
Python/ONNX/C++ 独立 Worker 进程

通信：
JSON Lines 控制协议
首版文件传图，后续共享内存

算法能力：
静态计数、动态计数、流水线计数、缺陷、行为、OCR、分割

运行设备：
CPU 保底，核显可选，NVIDIA GPU 加速

现场使用：
算法包导入 + 工程师配置 + 视觉配方 + 操作员零调参

数据：
SQLite + 本地图片/视频 + 事件化审计

交付：
Windows x64 自包含安装包
```

架构总结：

> 设计上分层，运行时分进程，开发上不分家，交付上是一个软件；核心稳定，边缘可插拔，高风险进程隔离。
