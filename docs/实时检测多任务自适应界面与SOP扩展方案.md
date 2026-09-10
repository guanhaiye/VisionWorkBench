# 实时检测多任务自适应界面与 SOP 扩展方案（实施修订版）

> 文档状态：可进入详细设计与分阶段实施  
> 修订日期：2026-09-08  
> 适用项目：VisionWorkbench

## 1. 文档目的

本文档用于规划 VisionWorkbench 实时检测页面的下一阶段改造。

核心目标是：

> 在保留综合视觉软件定位的前提下，让实时检测页面根据任务类型和任务配置自动适配；SOP 工序检测作为一种可选工作流接入，而不是把整个软件改造成只服务于 SOP 的单一系统。

本文档覆盖：

- 当前能力和边界；
- 多任务类型的页面适配方案；
- SOP 工序检测的扩展方式；
- 前端、领域模型、算法事件、数据库和历史追溯设计；
- 分阶段实施计划；
- 验收标准、风险和回滚策略。

本文档是实施设计基线，不代表相关代码已经完成。开发过程中如需改变本文定义的聚合边界、运行拓扑、持久化关系或兼容策略，应先更新本文并完成评审。

---

## 2. 背景与设计原则

### 2.1 软件定位

VisionWorkbench 是综合型工业视觉工作台，覆盖：

- 目标检测；
- 语义分割；
- 实例分割；
- 行为识别；
- OCR/文字识别；
- 条码和二维码识别；
- 计数和跨线计数；
- 模型训练、测试和数据标注；
- 实时检测、批次统计和历史追溯；
- TCP/IP 通讯和设备集成。

因此，实时检测页面不能只围绕“SOP 当前步骤”设计。

### 2.2 设计原则

1. **通用能力与任务专属能力分离**  
   视频预览、开始/暂停、单次检测、批次、日志和结果状态属于通用能力；步骤进度、码值匹配、人员行为等属于任务专属能力。

2. **SOP 是可选编排层**  
   SOP 可以编排目标检测、OCR、条码、二维码和行为事件，但普通任务不需要绑定 SOP。

3. **现有任务不被破坏**  
   已有任务配置、模型、规则、实时检测、历史数据和插件协议必须继续可用。

4. **任务类型决定默认界面，任务配置决定具体组件**  
   页面不能仅依赖一个固定的实时检测面板，也不能为每个任务复制一套完全独立的运行代码。

5. **算法输出与业务流程解耦**  
   算法只负责输出检测结果和事件；SOP 状态机负责解释事件并推进工序。

6. **所有界面状态必须可追溯**  
   整体结果、步骤结果、原图、结果图、异常原因、模型版本和规则版本需要能够关联查询。

---

## 3. 当前系统能力与不足

### 3.1 当前已有能力

当前实时检测面板已经具备：

- 实时相机、图片目录和视频文件输入；
- 检测结果叠加显示；
- ROI 绘制和清除；
- 开始、暂停、单次检测和结束批次；
- 当前判定、检测数量、算法耗时和累计统计；
- 最近检测日志；
- OK/NG/人工复核等结果状态；
- 批次记录、历史记录和结果图；
- 目标检测规则、区域规则、置信度规则和 Python 后处理；
- 行为识别的人员跟踪、区域闯入、滞留、聚集、跌倒和自定义模型事件。
- 统一 `AlgorithmOutput` 结果结构及已有 `VisionEvent` 事件原语；
- `VisionEvents` 的数据库实体、索引和随检测记录写入能力；
- 多工位运行协调、相机独占检查和每工位独立检测运行实例。

相关代码：

- 通用实时任务面板：`src/VisionWorkbench.App/LiveTaskPanel.xaml`；
- 实时检测页面：`src/VisionWorkbench.App/LivePage.xaml`；
- 任务领域模型：`src/VisionWorkbench.Domain/Recipes.cs`；
- 行为识别引擎：`src/VisionWorkbench.Domain/BehaviorRecognition.cs`；
- 历史记录页面：`src/VisionWorkbench.App/HistoryPage.xaml`。

### 3.2 当前不足

当前系统还没有：

- SOP 定义、版本和步骤实体；
- 步骤顺序和步骤状态机；
- 当前步骤、已完成步骤、超时步骤和错序步骤；
- 将多个检测事件组合成一个工序完成条件的机制；
- 每个 SOP 步骤的证据和耗时记录；
- SOP 专用实时面板；
- “目标检测任务 + SOP 编排”这种可选绑定关系。
- 面向 OCR/条码/二维码的完整实时结果原语和统一适配器；
- SOP 跨帧产品周期与现有逐帧 `InspectionRecord` 之间的关联模型。

需要特别说明：系统已经存在 `VisionEvent`，后续工作是兼容扩展现有契约，而不是创建第二个同名事件类型。

### 3.3 视频功能的准确定位

参考视频展示的功能不是单纯的人员行为识别，而是：

```text
目标/部件检测
    + OCR/码制识别
    + 可选动作识别
    + 工序事件判断
    + SOP 顺序状态机
    + 产品级 OK/NG
    + 过程证据与追溯
```

因此，不能直接把现有“行为识别页面”改名为 SOP 页面，也不能用行为识别模块替代 SOP 状态机。

---

## 4. 目标总体架构

### 4.0 第一版运行拓扑约束

第一版采用以下明确边界：

- 一个 `SopRun` 只属于一个项目、工位、任务和批次；
- 一个工位运行实例持有一个相机会话和一个主算法会话；
- 同一运行管线可以通过插件输出或宿主适配器产生检测、分割、OCR、码值和行为等多类结构化结果；
- 第一版不支持一个 SOP 同时订阅多个独立工位，也不支持跨设备事件直接共同推进同一产品周期；
- 将来若增加多任务事件汇聚，必须另行定义时钟同步、帧关联、事件乱序、来源掉线和一致性策略。

这一约束不影响页面同时展示多个任务。每个任务面板仍拥有独立运行上下文，但不同面板不会共同推进同一个 `SopRun`。

### 4.1 分层结构

```text
┌─────────────────────────────────────────────┐
│ 实时检测工作区 LiveWorkspace                 │
│                                             │
│  通用运行区                                  │
│  - 视频预览                                  │
│  - 开始/暂停/单次/批次                       │
│  - 整体结果/日志/耗时                         │
│                                             │
│  任务专属视图                                │
│  - 检测/计数视图                              │
│  - 分割/缺陷视图                              │
│  - 行为识别视图                               │
│  - OCR/条码/二维码视图                        │
│  - SOP 工序视图                               │
└─────────────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────┐
│ 工位运行协调层 StationRunCoordinator          │
│ - 生命周期管理                               │
│ - 输入源管理                                 │
│ - 模型会话管理                               │
│ - 结果发布                                   │
│ - 批次和记录管理                             │
│ - 可选 SOP 产品周期协调                       │
└─────────────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────┐
│ 统一结果与事件层                              │
│ AlgorithmOutput / VisionEvent / DomainResult │
└─────────────────────────────────────────────┘
                       │
          ┌────────────┴────────────┐
          ▼                         ▼
   通用规则判定                  可选 SOP 状态机
   OK/NG/Review                  工序推进与追溯
```

### 4.2 页面组成

实时检测页面拆成两类组件：

#### 公共运行组件

所有任务都可以使用：

- 输入源和任务选择；
- 开始、暂停、停止、单次检测；
- 视频/图像预览；
- 检测框、掩膜、关键点等可视化叠加；
- 整体结果；
- 算法耗时和 FPS；
- 批次统计；
- 最近检测日志；
- 错误提示和资源状态。

#### 任务专属组件

根据任务能力动态加载：

- 目标检测：类别统计、计数模式、检测线、规则结果；
- 分割：掩膜、面积、轮廓和缺陷等级；
- 行为识别：人员、关键点、行为标签、区域事件、持续时间；
- OCR：文本内容、匹配结果、字符置信度；
- 条码/二维码：码值、格式、校验和匹配状态；
- SOP：步骤列表、当前步骤、完成进度、步骤证据和工序耗时。

---

## 5. 任务类型与页面适配矩阵

| 任务类型/配置 | 默认实时视图 | 重点信息 | 是否需要 SOP |
|---|---|---|---|
| 目标检测 | DetectionLiveView | 类别、数量、置信度、ROI、规则结果 | 可选 |
| 目标检测 + 计数 | CountingLiveView | 总数、去重、正反向、检测线、批次 | 可选 |
| 语义分割 | SegmentationLiveView | 掩膜、区域比例、缺陷结果 | 可选 |
| 实例分割 | SegmentationLiveView | 实例数量、轮廓、面积、缺陷 | 可选 |
| 行为识别 | BehaviorLiveView | 人员框、关键点、行为、事件持续时间 | 可选 |
| OCR/文字识别 | TextRecognitionLiveView | 识别文本、匹配规则、字符置信度 | 可选 |
| 条码/二维码 | CodeRecognitionLiveView | 码值、类型、重复、匹配结果 | 可选 |
| SOP 编排任务 | SopLiveView | 工序状态、当前步骤、步骤证据、产品结果 | 是 |
| 普通插件任务 | GenericLiveView | 插件输出、通用结果和日志 | 否 |

### 5.1 任务类型不是唯一依据

同一个目标检测模型可能有两种运行方式：

```text
目标检测任务
    ├── 不绑定 SOP → 通用检测界面
    └── 绑定 SOP   → SOP 工序界面
```

因此推荐在 `Recipe` 中增加可选的 SOP 绑定，而不是新增一个强制替代所有任务的 `SopTaskType`。

建议使用单一绑定对象，避免 ID、版本和模式分别演进：

```csharp
public sealed record SopBinding
{
    public required string DefinitionId { get; init; }
    public required int Version { get; init; }
    public SopRunMode RunMode { get; init; } = SopRunMode.StrictOrder;
}

public sealed record Recipe
{
    // 其他现有字段保持不变。
    public SopBinding? Sop { get; init; }
    public bool IsSopEnabled => Sop is not null;
}
```

这样既能保留原有任务类型，又能让不同类型的单任务运行管线绑定 SOP。`TaskEntity` 增加可空的 `WorkflowJson` 保存绑定；旧记录缺少该字段时按普通任务加载。禁止通过改写 `InspectionTaskType` 实现 SOP。

---

## 6. SOP 领域模型设计

### 6.1 SOP 定义

```csharp
public sealed record SopDefinition
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string ProductCode { get; init; } = "";
    public int Version { get; init; } = 1;
    public SopDefinitionStatus Status { get; init; } = SopDefinitionStatus.Draft;
    public IReadOnlyList<SopStep> Steps { get; init; } = [];
}
```

状态建议：

- `Draft`：编辑中；
- `Published`：可用于生产；
- `Retired`：停止使用，但历史记录仍可查询。

### 6.2 SOP 步骤

```csharp
public sealed record SopStep
{
    public required string Id { get; init; }
    public int Order { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool Required { get; init; } = true;
    public bool EnforceOrder { get; init; } = true;
    public double TimeoutSeconds { get; init; } = 30;
    public int MinimumStableFrames { get; init; } = 3;
    public IReadOnlyList<SopCondition> Conditions { get; init; } = [];
    public IReadOnlyList<SopReferenceImage> ReferenceImages { get; init; } = [];
}
```

### 6.3 步骤条件

步骤条件需要支持多种来源：

```csharp
public enum SopConditionKind
{
    ObjectPresent,
    ObjectAbsent,
    ObjectCount,
    ObjectInRegion,
    ObjectStable,
    TextEquals,
    CodeEquals,
    BehaviorStarted,
    BehaviorCompleted,
    RegionChanged,
    CustomEvent,
}
```

每条条件至少包含：

- 来源结果类型、适配器或同一运行管线中的模型节点；
- 类别/文本/码值/事件类型；
- 最低置信度；
- ROI 或区域；
- 持续帧数或持续时间；
- 是否必须满足；
- 失败时是 NG、人工复核还是等待。

### 6.4 SOP 运行实例

一件产品或一轮工序对应一个 `SopRun`：

```csharp
public sealed record SopRun
{
    public long Id { get; init; }
    public required string SopDefinitionId { get; init; }
    public int SopVersion { get; init; }
    public required string DefinitionHash { get; init; }
    public required string DefinitionSnapshotJson { get; init; }
    public required string ProjectId { get; init; }
    public required string StationCode { get; init; }
    public long TaskId { get; init; }
    public long? BatchId { get; init; }
    public required string CycleId { get; init; }
    public string? ProductId { get; init; }
    public SopRunStatus Status { get; init; }
    public int CurrentStepOrder { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? FailureReason { get; init; }
}
```

每个步骤对应一条 `SopStepResult`，保存：

- 步骤状态；
- 开始/完成时间；
- 耗时；
- 条件结果；
- 算法置信度；
- 原图和结果图；
- 异常原因；
- 关联的检测记录 ID。

`SopRun` 是产品周期聚合根。一轮 SOP 跨越多帧，因此一个 `SopRun` 可以关联多条 `InspectionRecord` 和多条 `VisionEvent`。检测记录不能作为 `SopRun` 的父实体。

---

## 7. SOP 状态机设计

### 7.1 状态

```text
Idle
  ↓ 开始检测
WaitingForStep
  ↓ 满足步骤条件
StepInProgress
  ↓ 连续稳定帧满足
StepCompleted
  ↓ 还有下一步
WaitingForNextStep
  ↓ 所有步骤完成
CompletedOk
```

异常分支：

```text
WaitingForStep ──超时──> NgTimeout
StepInProgress ──错误──> NgConditionFailed
任意步骤 ──错序──> NgWrongOrder
任意步骤 ──人工复核──> ReviewRequired
```

### 7.2 稳定判定

不能只根据单帧检测结果立即完成步骤，需要防止抖动：

- 连续 N 帧满足条件；
- 或在 T 秒内满足最小出现次数；
- 目标位置变化小于阈值；
- 目标置信度持续高于阈值；
- 目标离开后才能允许下一件产品开始。

稳定判定属于当前步骤内部状态。条件首次满足后进入 `Stabilizing`；中途失配按步骤配置清零或衰减；达到稳定帧数/持续时间后才产生幂等的 `StepSatisfied` 事件。不得把每个满足帧都当作一次步骤完成。

### 7.2.1 状态转移基线

| 当前状态 | 输入 | 条件 | 动作 | 新状态 |
|---|---|---|---|---|
| `Idle` | `CycleStarted` | 绑定已发布 SOP | 创建运行和步骤快照 | `WaitingForStep` |
| `WaitingForStep` | 条件部分满足 | 当前步骤事件 | 建立稳定窗口 | `Stabilizing` |
| `Stabilizing` | 条件持续满足 | 达到帧数/时长 | 写步骤结果和证据 | `StepCompleted` |
| `Stabilizing` | 条件失配 | 未超过容错 | 清零或衰减稳定窗口 | `WaitingForStep`/`Stabilizing` |
| `StepCompleted` | 内部推进 | 存在下一必需步骤 | 设置下一步开始时间 | `WaitingForStep` |
| `StepCompleted` | 内部推进 | 所有必需步骤完成 | 生成产品结论 | `CompletedOk` |
| 任意运行态 | `Timeout` | 当前步骤超时 | 固化失败原因和证据 | `NgTimeout` |
| 任意运行态 | 非当前步骤完成事件 | 严格顺序 | 固化错序信息 | `NgWrongOrder` |
| 任意运行态 | `ReviewRequested` | 规则要求复核 | 暂停自动推进 | `ReviewRequired` |
| `ReviewRequired` | `ReviewApproved` | 权限和审计通过 | 记录操作者及原因 | 原等待态/下一步 |
| 可恢复终态 | `ReworkRequested` | 策略允许 | 保留旧结果并创建重做尝试 | `WaitingForStep` |
| 任意非终态 | `CycleAborted` | 停机/人工取消 | 写中止原因 | `Aborted` |

所有输入必须携带稳定事件 ID。状态机按 `SopRunId + EventId` 去重，并按照帧序号和时间戳处理迟到事件；终态之后的普通算法事件只归档，不再改变结果。

### 7.3 产品周期边界

需要明确一件产品的开始和结束：

- 手动点击开始一件；
- 由入口目标触发；
- 由相机触发器或 PLC 触发；
- 由“复位/取走产品”事件触发；
- 一件产品完成后自动等待下一件。

第一版默认提供三种互斥启动策略：`Manual`、`ExternalTrigger`、`PresenceEdge`。每个任务只能选择一种主策略；PLC/TCP 重复请求通过 `CycleId` 幂等。`PresenceEdge` 必须观察到“产品进入”才启动，并在终态后观察到“产品离开”才允许下一周期，防止同一产品重复建档。

相机断流、算法进程退出或应用正常关闭时，活动周期进入 `Interrupted` 或 `Aborted`，不得自动记为 NG。应用重启后默认不自动续跑旧周期；只有保存完整状态快照并通过设备现场确认后才允许恢复。

周期边界必须和批次区分：

- 批次：一段生产任务的集合；
- 产品周期：批次中的一件产品；
- SOP 步骤：产品周期内部的工序。

---

## 8. 实时检测页面改造方案

### 8.1 通用布局

建议保留现有页面的通用区域：

```text
┌─────────────────────────────────────────────┐
│ 任务选择 | 开始 | 暂停 | 单次 | 批次 | 设置     │
├───────────────────────┬─────────────────────┤
│                       │ 任务专属信息区       │
│    实时视频/图像       │                     │
│    检测框/掩膜/关键点  │ 由任务类型动态生成   │
│                       │                     │
├───────────────────────┴─────────────────────┤
│ 通用状态、FPS、耗时、检测日志                 │
└─────────────────────────────────────────────┘
```

### 8.2 目标检测/计数视图

显示：

- 本帧目标数量；
- 目标类别统计；
- 累计数量；
- 动态去重或跨线计数；
- 检测线方向；
- ROI 和规则结果；
- 当前批次统计。

### 8.3 行为识别视图

显示：

- 人员框和跟踪 ID；
- 关键点；
- 当前行为标签；
- 行为置信度；
- 区域闯入、滞留、聚集、跌倒事件；
- 事件开始时间、持续时间和恢复状态。

该视图不显示 SOP 步骤，除非行为任务同时被某个 SOP 步骤引用。

### 8.4 OCR/条码/二维码视图

显示：

- 当前识别文本或码值；
- 期望值和实际值；
- 匹配/不匹配状态；
- 识别置信度；
- 最近若干次识别记录；
- 重复码、空码和格式错误提示。

### 8.5 SOP 视图

显示：

- 整体 OK/NG/检测中；
- 当前产品编号或运行编号；
- 完成进度，例如 `3 / 5`；
- 当前步骤名称；
- 当前步骤耗时；
- 步骤列表和状态颜色；
- 每一步参考图/结果缩略图；
- 上一件产品结果；
- 当前产品的 NG 原因；
- 复位、重做、人工确认按钮。

示例：

```text
整体结果：检测中
当前步骤：03 放入上盖
步骤进度：2 / 5

01 下盖       已完成
02 安装堵头   已完成
03 放入上盖   进行中
04 粘贴标签   未完成
05 安装按钮   未完成
```

### 8.6 小窗口和多任务布局

页面需要继续支持多任务面板：

- 一个任务：面板占满可用宽度；
- 多个任务：根据窗口宽度自动一列或两列；
- 不同任务类型可以使用不同专属视图；
- 多任务面板之间不能共享 SOP 状态、计数器或模型会话；
- 需要避免固定宽度控件导致挤压和重叠。

---

## 9. 前端组件和代码组织建议

### 9.1 任务视图接口

建议新增统一选择接口。工厂负责选择展示描述/ViewModel，视图由 WPF `DataTemplate` 创建，避免工厂直接持有业务状态：

```csharp
public interface ILiveTaskViewFactory
{
    bool CanHandle(LiveTaskViewContext context);
    ILiveTaskPanelViewModel CreateViewModel(LiveTaskViewContext context);
}

public interface ILiveTaskPanelViewModel : IAsyncDisposable
{
    Task ActivateAsync(CancellationToken cancellationToken);
    Task DeactivateAsync(CancellationToken cancellationToken);
}
```

上下文至少包含：

```csharp
public sealed record LiveTaskViewContext
{
    public required Recipe Recipe { get; init; }
    public required LiveTaskCapabilities Capabilities { get; init; }
    public SopDefinition? SopDefinition { get; init; }
}
```

### 9.2 建议的视图组件

```text
LiveWorkspace
├── LiveCommonToolbar
├── LivePreviewSurface
├── LiveResultSummary
├── LiveLogPanel
├── DetectionLivePanel
├── CountingLivePanel
├── SegmentationLivePanel
├── BehaviorLivePanel
├── TextRecognitionLivePanel
├── CodeRecognitionLivePanel
└── SopLivePanel
```

不建议在 `LiveTaskPanel.xaml` 中继续堆叠所有任务类型的控件。原有面板可以先作为通用视图，再逐步拆分专属面板。

### 9.3 能力描述

除了任务类型，建议提供能力描述：

```csharp
public sealed record LiveTaskCapabilities
{
    public bool SupportsObjectDetections { get; init; }
    public bool SupportsMasks { get; init; }
    public bool SupportsTracking { get; init; }
    public bool SupportsKeypoints { get; init; }
    public bool SupportsText { get; init; }
    public bool SupportsCodes { get; init; }
    public bool SupportsCounting { get; init; }
    public bool SupportsBehaviorEvents { get; init; }
    public bool SupportsSopBinding { get; init; }
}
```

这样可以避免把界面完全写死在枚举判断中。

---

## 10. 算法结果与事件层改造

### 10.1 保留现有算法输出

现有 `AlgorithmOutput` 继续作为算法层基础输出，包含：

- 检测对象；
- 跟踪对象；
- 分割结果；
- 关键点；
- 文本和码制结果；
- 算法耗时；
- 原始扩展字段。

### 10.2 兼容扩展现有统一视觉事件

系统已经存在 `VisionWorkbench.Contracts.Results.VisionEvent`，也已经在 `AlgorithmOutput.Events` 和数据库中使用。不得新增第二个同名类型。建议以向后兼容方式增加可选字段：

```csharp
public sealed record VisionEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public VisionEventPhase Phase { get; init; }
    public VisionEventSeverity Severity { get; init; }
    public string? SubjectId { get; init; }
    public string? RegionId { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? EvidenceImagePath { get; init; }

    // 新增字段均为可选，保证旧 Worker 输出仍可反序列化。
    public long? SourceTaskId { get; init; }
    public string? SourceStationCode { get; init; }
    public long? FrameSequence { get; init; }
    public NormalizedRect? Box { get; init; }
    public string? AttributesJson { get; init; }
}
```

OCR 和码值不应依赖自由文本 `Message`。在 `AlgorithmOutput` 增加明确的 `TextRecognitions` 和 `CodeRecognitions` 集合，再由宿主适配器按 SOP 条件产生事件。协议 SchemaVersion 升级时，新字段保持可选，旧插件继续按现有字段运行。

事件来源可以是：

- 目标检测适配器；
- OCR 适配器；
- 条码/二维码适配器；
- 行为识别适配器；
- Python 后处理脚本；
- 外部 PLC/TCP 事件。

事件适配发生在应用层，状态机只接收标准化 `SopInputEvent`。该输入信封包含 `SopRunId`、`EventId`、来源、帧序号、发生时间及原始载荷引用，用于去重、排序和审计。外部 PLC/TCP 信号不得伪装成算法输出。

### 10.3 SOP 不直接读取 UI 文本

SOP 状态机必须消费结构化事件，不能通过查找界面上的字符串判断步骤完成。这样可以保证：

- UI 改版不影响业务逻辑；
- 在完整保存事件流、顺序和版本快照时，历史回放可以重复计算；
- 自动化测试可以直接输入事件；
- 多种算法都能接入相同的 SOP 条件。

---

## 11. 数据库与历史追溯

### 11.1 新增表建议

| 表 | 作用 |
|---|---|
| `SopDefinitions` | SOP 名称、产品、版本、发布状态 |
| `SopSteps` | SOP 步骤、顺序、超时、条件配置 |
| `SopStepReferences` | 步骤参考图和说明 |
| `SopRuns` | 每件产品/每轮 SOP 运行实例 |
| `SopStepResults` | 每个步骤的结果和耗时 |
| `SopEvidence` | 步骤证据图、异常图、结果图 |
| `VisionEvents` | 复用现有表，补充 SOP 运行关联和来源字段 |

关键约束和索引：

- `SopDefinitions(Code, Version)` 唯一；已发布版本不可原位修改；
- `SopSteps(SopDefinitionId, Order)` 唯一；
- `SopRuns(ProjectId, StationCode, CycleId)` 唯一，用于外部触发幂等；
- `SopRuns(BatchId, StartedAtUtc)`、`SopRuns(Status, StartedAtUtc)` 建索引；
- `SopStepResults(SopRunId, StepId, Attempt)` 唯一；
- `InspectionRecords.SopRunId`、`VisionEvents.SopRunId` 可空并建索引；
- 删除策略以限制删除为默认。已被运行记录引用的 SOP 版本只能停用，不能物理删除；
- 删除产品周期前必须按数据保留策略处理证据文件和关联事件，不能只删除数据库行。

### 11.2 与现有表关联

```text
Batch
 ├── InspectionRecord（普通检测或尚未归入产品周期）
 └── SopRun（一个产品周期）
      ├── SopStepResult
      │    └── SopEvidence → InspectionRecord
      ├── InspectionRecord [0..N]
      └── VisionEvent [0..N]
```

现有普通检测记录不需要强制创建 SOP 数据；只有绑定 SOP 的任务才创建 `SopRun` 和步骤结果。

每个 `SopRun` 必须固化 `DefinitionSnapshotJson`、定义哈希、Recipe 版本、插件版本、模型版本、规则版本和状态机引擎版本。只有保存了完整事件流、顺序信息及这些快照时，历史页面才可以提供“重新计算”；否则只能展示当时结果，不能声称可重复计算。

### 11.3 数据库升级与回滚

当前项目采用 `EnsureCreated` 加幂等 SQL 补丁维护旧 SQLite 数据库。SOP 实施不能只依赖更新 EF 模型，必须：

1. 在升级前创建数据库备份并校验可读性；
2. 用新的 SchemaMigration 版本事务性创建 SOP 表、索引和可空关联列；
3. 先增加结构，再发布读取兼容代码，最后开放 SOP 写入；
4. 旧任务的 `WorkflowJson` 为空时按普通任务处理，不批量生成 SOP 数据；
5. 升级失败时回滚数据库事务，并保留原数据库和证据目录；
6. 应用回退版本必须能够忽略新表和新 JSON 可选字段；
7. 在真实旧库副本上测试升级、重复升级、异常中断和回退。

### 11.4 追溯页面

历史记录页面应增加筛选：

- 普通检测/SOP 检测；
- SOP 名称和版本；
- 产品编号；
- 当前步骤；
- 整体结果；
- NG 原因；
- 时间范围；
- 批次。

详情页显示：

- 产品整体结果；
- 步骤时间轴；
- 每一步状态；
- 每一步证据图；
- 算法和规则版本；
- 原图、标注图和日志；
- CSV/报告导出。

---

## 12. 任务配置改造

### 12.1 普通任务配置不变

目标检测、分割、行为识别、OCR、条码和二维码任务仍按当前方式创建和保存。

### 12.2 增加可选 SOP 绑定区

在任务配置页面增加：

```text
实时工作流
  □ 普通检测
  □ 绑定 SOP 流程

SOP： [选择 SOP]
版本：[已发布版本]
运行模式：[严格顺序 / 允许跳步 / 人工确认]
```

只有勾选“绑定 SOP 流程”后，才显示 SOP 相关配置。

### 12.3 SOP 编辑器

建议单独增加“流程管理/SOP 管理”页面，而不是把复杂步骤全部塞进任务配置页面。

页面包括：

- SOP 列表；
- 草稿、发布、停用；
- 步骤排序；
- 步骤条件；
- 参考图；
- 流程预览；
- 版本复制；
- 发布前校验。

---

## 13. 与行为识别的关系

### 13.1 行为识别的职责

行为识别负责回答：

- 人在哪里；
- 人是否进入区域；
- 人是否滞留；
- 是否聚集；
- 是否跌倒；
- 自定义动作是否发生。

### 13.2 SOP 的职责

SOP 负责回答：

- 当前产品处于哪一步；
- 这一步是否完成；
- 是否按顺序完成；
- 是否超时或漏步；
- 整件产品是否合格；
- 下一步应该等待什么事件。

### 13.3 可选组合

SOP 步骤可以引用行为事件，例如：

```text
步骤：按下启动按钮
条件：行为事件 button_press_started
      + 检测到按钮目标
      + 持续 300ms
```

但普通行为识别任务不应自动显示 SOP 步骤面板。

---

## 14. 实施阶段

### 阶段零：契约、聚合边界和数据库迁移基线

目标：先消除实现歧义，再进行界面拆分。

工作内容：

- 确认第一版单工位、单运行管线边界；
- 兼容扩展现有 `VisionEvent`，增加 OCR/码值结果原语；
- 定义 `SopRun`、步骤结果、证据和逐帧记录的关联；
- 定义 `SopBinding`、发布版本、快照和哈希；
- 编写状态转移表、周期边界和幂等规则；
- 编写数据库升级与回退测试。

验收：

- 不存在第二个 `VisionEvent` 类型；
- 旧插件结果和旧任务 JSON 可以加载；
- 一个产品周期能够关联多条检测记录；
- 核心实体和迁移方案通过评审。

### 阶段一：通用实时页面拆分

目标：不改变业务结果，只改善架构。

工作内容：

- 抽取通用工具栏；
- 抽取预览区；
- 抽取通用结果区；
- 抽取日志区；
- 增加任务视图选择器及 ViewModel 激活/释放生命周期；
- 保留现有 `LiveTaskPanel` 作为默认视图。

验收：

- 现有目标检测、行为识别和视频输入功能不受影响；
- 窗口缩放和多任务布局正常；
- 现有历史记录继续产生。

### 阶段二：任务专属视图

工作内容：

- 目标检测/计数视图；
- 分割视图；
- 行为识别视图；
- OCR/条码/二维码视图；
- 根据任务类型自动切换。

验收：

- 不同任务只显示相关控件；
- 任务切换不会残留上一任务的状态；
- 每个面板有独立模型会话和统计。

### 阶段三：SOP 领域模型和状态机

工作内容：

- 增加 SOP 定义、步骤、条件和版本；
- 增加 SOP 运行实例和步骤结果；
- 增加事件到条件的匹配器；
- 完成稳定帧、超时、错序和复位逻辑；
- 为状态机编写单元测试。
- 状态机输入支持事件幂等、迟到事件处理和终态保护；
- 建立产品周期与逐帧检测记录的关联。

验收：

- 可以模拟完整五步流程；
- 漏步、错序、超时会正确进入 NG；
- 所有步骤完成后才能进入整体 OK。

### 阶段四：SOP 配置和实时界面

工作内容：

- SOP 编辑器；
- 任务绑定 SOP；
- SOP 实时专属面板；
- 步骤证据和缩略图；
- 产品周期、复位和返工操作。

验收：

- 普通任务仍使用普通页面；
- 绑定 SOP 的任务进入 SOP 页面；
- 同一个软件可以并行管理不同类型任务。

### 阶段五：历史追溯和外部通讯

工作内容：

- SOP 过程历史；
- 步骤级证据；
- 报表导出；
- TCP/IP/PLC 的开始、复位、完成、OK/NG 信号；
- 审计和模型版本记录。

验收：

- 任意 NG 产品可以定位到具体步骤和证据；
- 外部设备可以获得稳定的周期结果；
- SOP 版本变化不影响旧记录解释。

---

## 15. 测试方案

### 15.1 单元测试

至少覆盖：

- 步骤按顺序完成；
- 步骤乱序；
- 缺失步骤；
- 超时；
- 连续稳定帧；
- 目标抖动；
- 产品完成后自动复位；
- 重做当前步骤；
- 人工确认后继续；
- SOP 版本切换。

### 15.2 集成测试

使用图片序列或视频模拟：

- 完整 OK 流程；
- 标签缺失；
- 堵头漏装；
- 上盖顺序错误；
- 外观缺陷；
- OCR 错误；
- 同时出现多个产品；
- 相机断流和模型异常。

### 15.3 UI 测试

测试窗口：

- 最小窗口；
- 默认窗口；
- 最大化窗口；
- 单任务；
- 多任务；
- 普通任务和 SOP 任务混合；
- 侧栏展开/收起；
- 页面切换后资源释放。

### 15.4 性能测试

重点指标：

- 单帧端到端延迟；
- 事件到步骤状态更新延迟；
- UI 刷新频率；
- 长时间运行内存增长；
- 证据图写入速度；
- 多任务并行时 CPU/GPU 占用。

视频中的“200ms 内响应”只能作为目标指标，必须结合当前硬件、模型和输入分辨率实测，不能直接承诺固定数值。

---

## 16. 风险与控制措施

| 风险 | 影响 | 控制措施 |
|---|---|---|
| 将 SOP 逻辑写死在实时页面 | 普通任务被复杂化 | SOP 使用独立状态机和专属视图 |
| 直接修改现有任务类型 | 旧任务无法加载 | 使用可选 SOP 绑定，保持旧枚举兼容 |
| 单帧误检导致步骤完成 | 误放行 | 稳定帧、持续时间和复核机制 |
| 算法输出格式不统一 | SOP 难以接入不同模型 | 增加统一 VisionEvent 层 |
| 重复定义现有事件契约 | Worker 和历史数据不兼容 | 只兼容扩展现有 `VisionEvent`，新增字段保持可选 |
| 产品周期错误绑定单帧记录 | 无法形成完整步骤证据链 | `SopRun` 作为聚合根并关联多条检测记录 |
| 多来源任务缺少同步语义 | 事件错序或错误推进 | MVP 限定单工位单管线，跨任务汇聚另行设计 |
| 多任务状态串线 | 统计和结果错误 | 每个面板独立上下文、会话和状态机 |
| SOP 版本变化影响历史 | 无法准确追溯 | 运行记录保存 SOP 版本和规则快照 |
| 证据图过多占用磁盘 | 长期运行失败 | 按 OK/NG 策略、保留周期和容量告警管理 |
| 复杂 UI 继续堆在单个 XAML | 维护困难 | 公共组件 + 任务专属视图工厂 |

---

## 17. 推荐的第一版 MVP

第一版不建议一次实现所有高级动作识别。优先完成：

1. 普通实时检测页面保持可用；
2. 任务类型对应不同的专属结果面板；
3. 增加 SOP 定义和五步流程；
4. 使用目标检测、OCR、条码和区域规则驱动 SOP；
5. 实现顺序、稳定帧、超时、NG 和复位；
6. 保存步骤结果和证据图；
7. 在历史记录中查看完整 SOP 过程。

第一版明确不包含：跨工位 SOP、多个独立任务共同推进一个产品周期、分布式事件总线、运行中修改已发布 SOP、以及缺少完整事件流时的历史重算。

动作识别模型作为第二阶段能力接入，用于无法通过静态目标状态判断的动作，例如拿取、安装、按压和粘贴。

---

## 18. 最终验收标准

完成后应满足：

- 普通目标检测任务仍显示通用检测界面；
- 行为识别任务显示人员和行为专属信息；
- OCR/码制任务显示文本或码值专属信息；
- 绑定 SOP 的任务显示工序专属信息；
- 一个软件实例可以管理不同类型的实时任务；
- 单个任务之间的模型、计数、日志和 SOP 状态相互隔离；
- SOP 步骤可以按顺序推进、失败、超时和复位；
- 重复、迟到和终态后的事件不会造成步骤重复完成或结果反转；
- 一个产品周期能够关联并查询其全部检测帧、事件和步骤证据；
- 已发布 SOP 不可原位修改，历史运行保存完整版本快照和哈希；
- 相机断流、算法退出和人工中止不会被误记为产品 NG；
- 所有步骤完成并且检测规则合格后才能判定整体 OK；
- NG 可以定位到具体步骤、原因和证据；
- 历史记录可以按 SOP、版本、批次和产品周期查询；
- 窗口缩放时控件不重叠、不被固定宽度挤压；
- 原有非 SOP 任务、历史数据和插件协议保持兼容。

---

## 19. 结论

本次改造的正确方向不是“把实时检测页面改成 SOP 页面”，而是：

```text
综合实时检测工作区
    ├── 通用检测视图
    ├── 计数视图
    ├── 分割视图
    ├── 行为识别视图
    ├── OCR/码制视图
    └── 可选 SOP 工序视图
```

SOP 作为一种可绑定、可配置、可追溯的业务编排能力，复用现有视觉算法和实时检测基础设施，同时不改变 VisionWorkbench 作为综合工业视觉软件的定位。

第一版以“单工位、单运行管线、单产品周期聚合”为落地边界。未来的跨任务或跨设备编排建立在稳定的事件信封、时钟关联和一致性机制之上，不在本期通过 UI 拼接或共享可变状态实现。
