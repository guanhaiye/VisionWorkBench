# VisionWorkbench 交接文档

本文档用于帮助新的开发者或代码代理快速理解 VisionWorkbench 项目。当前仓库路径为：

`E:\AI执行软件\VisionWorkbench`

## 1. 项目定位

VisionWorkbench 是 Windows 本地 AI 视觉检测工作台，技术栈为 .NET 10 + WPF + SQLite。主要能力包括：

- 图片目录、视频文件、USB/工业相机输入。
- 实时检测、任务配置、规则判定、历史记录和结果回传。
- Python 算法插件通过独立 Worker 进程运行，主程序与算法进程使用 JSON Lines 通信。
- TCP/IP 项目作为外部设备触发入口，按不同指令匹配并执行绑定检测任务。
- TCP 触发支持异步 accepted 响应、同工位并发、requestId 幂等和最终结果回传。

## 2. 当前版本状态

- 最新相关提交：`03c2113 修正TCP并发超时与相机帧分配`
- 前置提交：`aa06d15 支持TCP同工位并发触发执行`
- 当前分支：`main`
- 当前交接文档生成前工作区为干净状态。
- 本地没有推送 Gitee。

## 3. 目录结构

| 目录 | 职责 |
|---|---|
| `src/VisionWorkbench.Contracts` | 协议消息、算法输入输出、插件清单、公共错误码 |
| `src/VisionWorkbench.Domain` | 规则引擎、计数、判定、ROI 等领域逻辑 |
| `src/VisionWorkbench.Cameras.Abstractions` | `ICameraProvider`、`ICameraSession`、`VideoFrame` 等相机抽象 |
| `src/VisionWorkbench.Cameras.Files` | 图片目录和视频文件虚拟相机，主要用于测试和离线检测 |
| `src/VisionWorkbench.Cameras.Usb` | USB 相机实现 |
| `src/VisionWorkbench.Cameras.Hikvision` | 海康工业相机实现 |
| `src/VisionWorkbench.Infrastructure` | Worker 进程、插件扫描、日志、临时文件等基础设施 |
| `src/VisionWorkbench.Algorithms` | 算法插件会话和算法管理器 |
| `src/VisionWorkbench.Persistence` | EF Core SQLite、实体和仓储 |
| `src/VisionWorkbench.Application` | 通用应用服务、帧调度、TCP 通信协议和执行调度 |
| `src/VisionWorkbench.App` | WPF 主程序、页面、TCP 任务执行桥接 |
| `workers` | Python SDK、示例算法 Worker 和插件测试 |
| `tests/VisionWorkbench.Tests` | xUnit 单元测试、协议测试、调度测试、服务测试和 UI smoke 测试 |

解决方案文件是 `VisionWorkbench.slnx`，项目使用 `global.json` 指定 .NET SDK `10.0.400`。

## 4. 启动入口与服务装配

主要入口：

- `src/VisionWorkbench.App/App.xaml.cs`
- `src/VisionWorkbench.App/AppServices.cs`

`AppServices.Initialize()` 负责装配数据库、仓储、算法管理器、相机服务和 TCP 服务。TCP 相关关系如下：

```text
AppServices
  ├─ TcpCommunication / ProjectCommunicationManager
  └─ TcpTaskExecutionService
       └─ LiveTaskPanel / 实时检测执行入口
```

`AppServices` 中将：

```csharp
TcpCommunication.TaskExecutor = TcpTaskExecution.ExecuteAsync;
```

因此 TCP 协议层只负责接收、匹配、排队和回传；实际检测由 `TcpTaskExecutionService` 进入实时检测执行链路。

## 5. TCP 通信链路

核心文件：

- `src/VisionWorkbench.Application/Communication/TcpCommunication.cs`
- `src/VisionWorkbench.App/TcpTaskExecutionService.cs`
- `src/VisionWorkbench.App/CommunicationPage.xaml.cs`
- `src/VisionWorkbench.Persistence/CommunicationRequestStore.cs`

### 5.1 接收流程

```text
TCP Server/Client Transport
  -> TcpConnectionSession
  -> TcpFrameDecoder
  -> TcpMessageCodec
  -> ProjectCommunicationManager
  -> TcpTaskMatcher
  -> CommunicationRequestStore.BeginAsync
  -> TcpStationExecutionScheduler.TryEnqueue
  -> 立即返回 accepted
  -> 后台 ObserveExecutionAsync
  -> 完成后按 requestId 回传结果
```

支持的匹配模式包括：

- `ExactText`
- `Prefix`
- `Regex`
- `JsonField`
- `Default`

常用 TCP 指令是 `execute` 或 `task`。任务规则配置在通信项目中绑定任务和匹配内容，规则名称只用于展示，不应作为任务身份或匹配条件。

### 5.2 accepted 与最终响应

合法指令进入调度器后应尽快返回：

```json
{"ok":true,"code":"accepted","requestId":"...","data":{"task":"..."}}
```

任务执行完成后，响应必须携带同一个 `requestId`。并发执行允许乱序完成，客户端必须按 `requestId` 关联响应，不能按连接上的先后顺序关联。

常见终态代码：

- `completed`
- `execution_timeout`
- `station_queue_full`
- `execution_unavailable`
- `request_id_conflict`
- `request_processing`

## 6. 同工位并发调度

`TcpStationExecutionScheduler` 位于 `src/VisionWorkbench.Application/Communication/TcpCommunication.cs`。

当前设计要点：

- 同一工位默认最大并发 `3`。
- 默认等待队列长度 `20`。
- 默认单次执行超时 `30` 秒。
- 每个 TCP 项目传入自己的 `Options`，不能用一个全局可变配置覆盖其他项目。
- 调度键至少包含 `ProjectCode + StationCode`，不同项目中同名工位相互隔离。
- 同一 `requestId`：执行中返回 `Processing`，完成后返回缓存结果。
- 队列满返回 `QueueFull`，不能进入执行函数。
- 项目停止时清理排队请求并等待相关 worker 退出。

调度器的 `Start` 回调用于在 accepted 已发送后放行执行。这样可以保证收包线程不等待检测完成，也能处理 accepted 发送失败时取消尚未正式启动的执行。

## 7. 实际检测执行与资源复用

`TcpTaskExecutionService` 负责把 TCP 任务转换为实时检测执行。

### 7.1 共享相机

同一个实际相机不能因并发触发而重复打开。当前实现通过 `SharedCameraRuntime` 复用相机，并使用：

`src/VisionWorkbench.Application/TriggerFrameDistributor.cs`

该分发器的原则：

- 相机只打开一次。
- 不使用无限帧队列。
- 空闲时最多保留一个最新帧。
- 每个请求按自己的触发时间等待触发之后的帧。
- 同一时刻多个等待者可以收到同一帧的独立通知。
- 更晚触发的请求不会错误消费早于其触发时刻的帧。

### 7.2 模型会话池

`TcpTaskExecutionService.ModelSessionPool` 使用独立算法会话池处理并发执行。每个执行实例租用一个算法会话，执行结束后按可复用状态归还；不可复用会话会被释放。

不要把同一个不明确线程安全的算法会话直接同时交给多个执行实例。若修改模型并发策略，必须补充并发测试。

## 8. requestId 幂等和数据库状态

持久化实现：

- `src/VisionWorkbench.Persistence/CommunicationRequestStore.cs`
- `CommunicationRequestEntity` 位于 `src/VisionWorkbench.Persistence/Entities.cs`

数据库为 SQLite，默认数据目录在 `%LOCALAPPDATA%\VisionWorkbench`，具体路径由应用设置决定。

`CommunicationRequestStore.BeginAsync` 以以下字段组成幂等键：

```text
ProjectCode + ClientId + RequestId
```

请求体哈希不一致时返回冲突；相同请求已完成时返回缓存 JSON；相同请求仍处理中时返回 processing；新请求会写入 `processing`。

完成时必须调用 `CompleteAsync`，写入完整响应 JSON，把数据库状态改为 `completed`。响应 JSON 应同时写入内存缓存和持久化存储，保证重发请求拿到完全一致的终态。

## 9. 当前尚待处理的第三轮复核问题

以下问题是本交接时用户刚提出、尚未在代码中完成的新一轮修改，接手后应优先处理：

1. `BeginAsync` 成功后，`TaskExecutor == null` 或 scheduler `QueueFull` 的分支必须调用 `CompleteAsync`，持久化与实际回包完全一致的 `execution_unavailable` 或 `station_queue_full` JSON。
2. 项目停止导致执行收到 `OperationCanceledException` 时，不能只写日志；应生成 `execution_canceled` 或 `project_stopped` 终态，写入 request store/cache。项目已经停止时可以不发送，但同一 `requestId` 后续查询或重发不能保持 `processing`。
3. accepted 响应发送失败后调用 `submission.Cancel` 的路径，也必须写入取消/发送失败终态，不能留下永久 `processing`。
4. `unsupported_command`、`task_not_configured` 等校验失败最好在 `BeginAsync` 之前返回；如果保留当前顺序，则所有已经 Begin 的分支都必须终结持久化状态。
5. 增加至少覆盖 queue full 和项目停止取消的持久化测试。推荐使用真实 `CommunicationRequestStore` 或抽取响应终结帮助方法，验证数据库最终状态和缓存 JSON。

修改这些逻辑时，要特别防止重复完成同一个 request 的竞态。建议集中一个“终结请求”帮助方法，负责：构造 JSON、写内存缓存、调用 `CompleteAsync`、按需发送，并保证异常不会阻止数据库落终态。

## 10. 测试入口

项目自带 SDK 位于 `.dotnet-sdk-10b`。优先使用它，不要混用系统 .NET 9 SDK：

```powershell
$env:DOTNET_ROOT = (Resolve-Path '.dotnet-sdk-10b').Path
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$dotnet = '.\.dotnet-sdk-10b\dotnet.exe'
```

构建：

```powershell
& $dotnet build VisionWorkbench.slnx -c Debug --no-restore
```

TCP 定向测试：

```powershell
& $dotnet test tests/VisionWorkbench.Tests/VisionWorkbench.Tests.csproj `
  -c Debug --no-build --no-restore `
  --filter 'FullyQualifiedName~TcpCommunicationTests|FullyQualifiedName~TcpStationExecutionSchedulerTests'
```

完整 .NET 测试：

```powershell
& $dotnet test tests/VisionWorkbench.Tests/VisionWorkbench.Tests.csproj `
  -c Debug --no-build --no-restore
```

Python Worker 测试：

```powershell
workers\.venv\Scripts\python -m pytest workers\sample-counter\tests -q
```

## 11. 当前测试基线

在提交 `03c2113` 上：

- Debug 构建通过：0 warnings、0 errors。
- TCP 定向测试通过：9/9。
- 完整 .NET 测试：115 passed、9 failed。
- 9 个失败来自既有 WPF UI smoke 测试，原因是测试环境缺少 `PageTextBlockBaseStyle` 资源，并非 TCP 调度测试失败。修改 TCP 代码后复测时仍需单独区分这类 UI 环境问题。

如果测试命令异常卡住，先检查由本次测试启动的进程：

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -in @('testhost.exe','vstest.console.exe') } |
  Select-Object ProcessId,Name,CommandLine
```

只处理命令行明确指向当前仓库测试输出目录的进程，不要批量结束所有 `dotnet` 或 MSBuild 节点。

## 12. 修改和提交约定

- 先检查 `git status --short`，不要覆盖其他未提交修改。
- 只提交本次任务涉及的文件。
- 修改后至少运行相关定向测试和 Debug build。
- 用户明确要求时才推送 Gitee；默认只保留本地 commit。
- 提交前运行 `git diff --check`。
- 最终报告应包含：修改摘要、文件列表、测试命令和结果、已知限制、commit hash、是否推送。

## 13. 推荐接手顺序

1. 阅读本文件和 `AGENTS.md`。
2. 查看 `TcpCommunication.cs` 的 `ProjectCommunicationManager`、`TcpStationExecutionScheduler` 和 `ObserveExecutionAsync`。
3. 查看 `CommunicationRequestStore.cs`，确认 request 的数据库状态流转。
4. 先补 requestStore 终态闭环测试，再修改实现。
5. 运行 TCP 定向测试和 build，最后再决定是否运行完整测试。
6. 检查 `git diff`，只提交当前任务改动，不 push。
