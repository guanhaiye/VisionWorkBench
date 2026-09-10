# 实时检测多任务自适应界面与 SOP 扩展实施结果报告

> 项目：VisionWorkbench  
> 实施日期：2026-09-08  
> 对照基线：[实时检测多任务自适应界面与SOP扩展方案.md](./实时检测多任务自适应界面与SOP扩展方案.md)  
> 当前发布目录：`artifacts/commercial/Release`

## 1. 实施结论

本次已完成方案第一版 MVP 的核心闭环：

```text
多任务实时工作区
    → 独立任务面板与自适应布局
    → 可选 SOP 定义
    → SOP 步骤与条件配置
    → 目标检测/视觉事件驱动状态机
    → 步骤进度、整体结果和过程快照
    → SopRun、步骤结果、检测记录关联追溯
```

本轮收尾同时完成了运行一致性修复：视觉规则、Python 后处理事件和 SOP 状态统一参与最终判定；SOP 未完成时不能把视觉 OK 直接放行，SOP 超时、错序或条件失败会覆盖视觉 OK。稳定帧要求所有必需条件在同一帧同时满足，缺少任一条件会重新计数；重复 EventId 不会重复推进。运行时增加独立 SOP 时钟，暂停策略为“暂停即暂停 SOP 计时”；终止当前产品后可从界面开始下一件，使用新的 `SopRunId` 和 `CycleId`，避免跨产品串状态。

软件仍保持综合视觉工作台定位。未绑定 SOP 的目标检测、计数、分割、行为识别、OCR、条码和二维码等任务不会被强制改造成 SOP 页面；只有绑定 SOP 的任务才显示 SOP 工序进度卡片。

当前版本已编译并发布到正式目录，运行文件为：

`G:\AI执行软件\VisionWorkbench\artifacts\commercial\Release\VisionWorkbench.exe`

`artifacts/commercial` 下目前只保留 `Release` 一个发布目录。

## 2. 方案条目实施状态

| 方案能力 | 状态 | 实施结果 |
|---|---|---|
| 多任务实时工作区 | 已完成 | 支持任务面板新增、移除、任务独立选择、独立运行上下文和工作区设置保存。 |
| 自适应窗口布局 | 已完成 | 支持网格、纵向、横向布局；网格在空间不足时自动使用单列，空间足够且多任务时使用两列；工具栏使用可换行布局。 |
| 通用实时检测能力 | 已完成 | 保留预览、开始、暂停、单次检测、结束批次、清零、ROI、统计和日志等公共能力。 |
| 任务类型适配 | 第一版已落地 | 面板会显示任务模式，保留已有行为识别、计数、分割等结果能力，并根据 SOP 绑定状态显示 SOP 专属卡片。独立的可插拔 ViewFactory 尚未拆出。 |
| SOP 定义和版本 | 已完成 MVP | 支持定义编号、名称、产品编号、版本、草稿/已发布/已停用状态。已发布版本禁止原位修改。 |
| SOP 步骤制作 | 已完成 MVP | 支持新增、删除和编辑步骤顺序、步骤编号、步骤名称、类别/事件 ID、置信度、稳定帧数和超时。 |
| SOP 条件和状态机 | 已完成核心能力 | 支持目标出现、目标缺失、数量、区域、稳定、文本、码值、行为和自定义事件等条件枚举；当前制作界面主要配置目标出现条件。 |
| 稳定帧、超时、错序 | 已完成 | 状态机支持稳定帧累积、超时、严格顺序、允许跳步和人工确认模式。重复事件按 EventId 去重，终态不会被普通事件反转。 |
| 最终判定统一 | 已完成 | 视觉规则、Python 后处理和 SOP 共同生成最终状态；SOP 未完成不放行，超时/错序/条件失败可覆盖视觉 OK。 |
| 产品终态落库与发布 | 已完成 | 帧内终态复用 `InspectionRecord.FinalResultJson`；无新帧超时写入 `SopRuns.Final*` 字段，并通过不带算法帧的产品结果信封发布，按周期幂等一次。 |
| 持久化待发布重放 | 已完成 | 启动和运行期间扫描 `FinalizedAtUtc != null && FinalPublishedAtUtc == null` 的周期；使用持久化 Claim、有界租约和 `ResultId` 幂等重放，不依赖当前状态机或当前产品。 |
| 暂停计时 | 已完成 | 状态机累计暂停区间，恢复后只消耗有效运行时间，暂停超过步骤超时阈值不会立即误超时。 |
| 进行中状态 | 已完成 | 新增 `processing` 状态；历史筛选、实时 UI 和统计均不把 SOP 中间帧当作 error 或质量终态。 |
| 多产品连续周期 | 已完成核心能力 | 当前产品进入终态后可“开始下一件”或“复位当前产品”；每件产品使用新的 `SopRunId`、`CycleId`、步骤状态和证据上下文。 |
| 任务绑定和解绑 | 已完成 | 已发布 SOP 可从 SOP 制作页直接绑定到检测任务，也可解绑；任务配置中的旧版内嵌 SOP 可自动收编到 SOP 目录。 |
| 实时 SOP 视图 | 已完成 MVP | 绑定 SOP 的任务显示当前步骤、完成进度、步骤状态、失败/超时/人工确认等状态。普通任务不显示该卡片。 |
| 统一视觉事件扩展 | 已完成 | 在现有 `VisionEvent` 基础上增加来源、帧序号、框、文本、码值、数量等可选字段，没有创建第二个同名事件类型。 |
| 运行实例和步骤结果 | 已完成 | 创建 `SopRun`，记录定义快照、版本、哈希、任务、工位、批次和周期；步骤结果写入 `SopStepResults`。 |
| 检测记录关联 | 已完成 | `InspectionRecord` 支持 `SopRunId`，检测记录保存 SOP 快照结果，视觉事件支持 SOP 运行关联。 |
| 过程证据 | 已完成基础能力 | NG/人工复核等结果可保存原图和标注图，普通 OK 仍按原有策略保存结构化数据。步骤级参考图和独立证据表仍需增强。 |
| 历史 SOP 专用页面 | 部分完成 | 数据层已具备运行和步骤结果，现有历史记录可继续查询检测记录；按 SOP 版本、产品周期、步骤时间轴和证据筛选的专用页面尚未完成。 |
| OCR/条码/行为事件接入 | 部分完成 | 状态机和事件信封已预留文本、码值、行为事件匹配能力；当前 SOP 编辑器的默认制作路径仍以 `object.present` 为主，完整适配器和专属视图待后续阶段补齐。 |
| PLC/TCP 周期控制 | 部分完成 | 原有 TCP/IP 通讯能力保持不变；SOP 的开始、复位、完成和周期幂等信号尚未形成完整的专用协议闭环。 |

## 3. 主要代码落点

### 3.1 实时检测工作区

- `src/VisionWorkbench.App/LivePage.xaml`
- `src/VisionWorkbench.App/LivePage.xaml.cs`
- `src/VisionWorkbench.App/LiveTaskPanel.xaml`
- `src/VisionWorkbench.App/LiveTaskPanel.xaml.cs`

实现要点：

- 每个任务使用独立 `LiveTaskPanel`，避免计数器、模型会话、日志和 SOP 状态串线；
- 同一个任务不能重复加入多个检测面板；
- 网格布局根据可用宽度自动选择一列或两列；
- SOP 卡片只有在当前任务存在 SOP 绑定时显示；
- 普通任务仍沿用原有通用检测面板。

### 3.2 SOP 领域和状态机

- `src/VisionWorkbench.Domain/SopWorkflow.cs`

已增加：

- `SopDefinition`、`SopStep`、`SopCondition`；
- `SopBinding`、`SopRun`、`SopStepResult`；
- SOP 状态、步骤状态、条件类型和运行模式枚举；
- `SopStateMachine`；
- 稳定帧、超时、错序、跳步、人工确认、复位和终态保护；
- `SopInputEvent` 结构化事件输入。

### 3.3 SOP 制作和目录

- `src/VisionWorkbench.App/SopPage.xaml`
- `src/VisionWorkbench.App/SopPage.xaml.cs`
- `src/VisionWorkbench.Application/SopDefinitionCatalogService.cs`
- `src/VisionWorkbench.App/ShellNavigation.xaml`

菜单入口为：

`工作台 → SOP流程制作与管理`

制作页支持：

1. 新建 SOP；
2. 编辑基础信息；
3. 编辑工序步骤；
4. 保存草稿和发布版本；
5. 发布版本保护；
6. 绑定/解绑检测任务；
7. 自动发现并收编旧任务中的内嵌 SOP。

当前 SOP 定义目录以配置目录下的 `sop-definitions.json` 保存；运行实例和步骤结果仍进入 SQLite 数据库。这样可以在不破坏现有任务 JSON 的前提下先完成可用的制作闭环。

### 3.4 任务、事件和运行链路

- `src/VisionWorkbench.Domain/Recipes.cs`
- `src/VisionWorkbench.Application/RecipeService.cs`
- `src/VisionWorkbench.Application/DetectionRunService.cs`
- `src/VisionWorkbench.Contracts/Results/Events.cs`

实现内容：

- `Recipe` 增加可选 SOP 绑定，不改变原有任务类型；
- 任务使用 `WorkflowJson` 保存 SOP 绑定；
- 检测输出中的目标和视觉事件转换为 `SopInputEvent`；
- Python 后处理结果支持追加 `result.events`，并在进入 SOP 状态机前转换为统一 `SopInputEvent`；
- 每次检测帧更新 SOP 快照；
- 结果状态和 SOP 状态共同参与最终结果展示、历史记录和发布链路；
- 运行时固定 SOP 定义快照并计算定义哈希，防止后续编辑影响历史运行解释。

### 3.6 运行时一致性和多产品周期

- `src/VisionWorkbench.Application/DetectionRunService.cs`
- `src/VisionWorkbench.App/LiveTaskPanel.xaml`
- `src/VisionWorkbench.App/LiveTaskPanel.xaml.cs`

运行服务在每个完整检测帧上一次性提交 SOP 输入，避免把不同帧的条件错误拼成一次满足；独立时钟负责无新帧时的超时判定。实时面板新增“开始下一件”和“复位当前产品”，并在终态时切换到新的产品周期。

产品终态分两条路径：有算法帧时，`InspectionRecord.Status`、`FinalResultJson.Decision` 和普通 `IResultPublisher` 信封使用同一个合并判定；无算法帧超时时，写入 `SopRunEntity.FinalStatus/FinalDecisionJson/FinalResultJson`，通过 `ProductResultEnvelope` 发布，不创建虚假的算法输出帧。

### 3.5 数据库和持久化

- `src/VisionWorkbench.Persistence/Entities.cs`
- `src/VisionWorkbench.Persistence/VisionDbContext.cs`
- `src/VisionWorkbench.Persistence/Repositories.cs`

已增加或扩展：

- `Tasks.WorkflowJson`；
- `InspectionRecords.SopRunId`、`WorkflowResultJson`；
- `SopRuns`；
- `SopStepResults`；
- `VisionEvents` 的 SOP 运行、来源、帧序号、文本、码值、数量和框字段；
- 运行、步骤、检测记录和视觉事件索引；
- 旧 SQLite 数据库的幂等字段补丁和表创建逻辑。

## 4. 验证结果

### 4.1 编译和发布

执行命令：

```powershell
G:\AI执行软件\.dotnet10\dotnet.exe publish `
  src\VisionWorkbench.App\VisionWorkbench.App.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o artifacts\commercial\Release
```

结果：发布成功，无编译错误；软件从 `Release` 正常启动，窗口标题为 `VisionWorkbench — AI 视觉工作台`。

### 4.2 核心回归测试

针对本次改造相关的状态机、最终判定和运行协调测试：

```text
通过：20
失败：0
跳过：0
```

覆盖内容包括：

- SOP 步骤按顺序完成；
- 稳定帧判定；
- 事件不满足时不误完成；
- 缺少条件或不同帧条件不会误完成稳定帧；
- 视觉 OK 与 SOP 未完成/失败时的最终判定；
- 严格模式错序；
- 步骤超时；
- 工位运行协调和资源隔离；
- 多产品周期隔离；
- 无新帧时由独立运行时钟触发超时。
- 暂停期间不消耗 SOP 超时预算；
- 帧内 `InspectionRecord`、最终 JSON 和发布信封判定一致；
- 无帧超时产品结果落库并且只发布一次；
- 正常帧完成 SOP 时也发布唯一产品级 OK/NG 结果；
- 时钟终态与随后到达的帧竞争时不重复发布；
- 产品结果发布失败后可以重试，临时并发跟踪结构不永久增长；
- 切换产品/创建新服务实例后仍可扫描并重放旧周期；
- 并发重放和重复扫描不会重复本地 JSONL 产品结果；
- 并发“开始下一件”只创建一个新 SOP 周期。

本次发布复核结果：

- Release 发布成功，构建 0 个警告、0 个错误；
- 正式程序已从 `G:\AI执行软件\VisionWorkbench\artifacts\commercial\Release\VisionWorkbench.exe` 启动；
- `artifacts/commercial` 下仅保留 `Release` 正式目录。

### 4.3 全量测试说明

当前环境执行全量测试得到：

```text
总计：102
通过：80
失败：22
```

失败项主要集中在两类环境问题：

1. Worker 集成测试和端到端算法测试读取到不存在的 Python 路径；当前 `workers\.venv\Scripts\python.exe` 也是指向该旧安装的失效启动器：
   `C:\Users\Public\Applications\AIDI_3.3.1_stable_20251130\python.exe`；
2. WPF UI Smoke 测试未加载完整应用资源字典，导致测试宿主中找不到 `PageTextBlockBaseStyle`。

这些失败属于测试环境依赖问题，不影响本次 20 项 SOP 相关测试和发布构建。后续应在具备有效 Python/Worker 和 WPF 资源初始化的测试环境中补跑全量回归，并增加 Python `result.events` 的真实 Worker 集成验证。

## 5. 与方案最终验收标准的差异

本次版本已达到“第一版 MVP”目标，但方案文档还规划了若干后续阶段能力，当前不应误认为已经全部完成：

- 尚未建立独立的 `DetectionLiveView`、`BehaviorLiveView`、`TextRecognitionLiveView` 等 ViewFactory 体系；
- SOP 定义和步骤目前使用 JSON 目录，尚未独立建成 `SopDefinitions`、`SopSteps`、参考图和证据表；
- 历史页面尚未提供 SOP 版本、产品周期、步骤时间轴和步骤证据的专用筛选/报表界面；
- OCR、条码、二维码和行为事件的 SOP 条件模型已预留，但完整实时适配器和制作控件仍需继续实现；
- PLC/TCP 的 SOP 周期开始、复位、完成和幂等协议尚未形成完整闭环；
- 目前运行启动时创建 SOP 周期并持续更新快照，复杂的 Manual/ExternalTrigger/PresenceEdge 产品周期策略尚需继续细化；
- 参考图、步骤级证据、返工尝试和人工审核审计仍需补充。

## 6. 使用验收路径

1. 启动 `artifacts/commercial/Release/VisionWorkbench.exe`；
2. 进入 `工作台 → SOP流程制作与管理`；
3. 点击“新建SOP”，填写流程基本信息；
4. 点击“新增步骤”，配置步骤编号、名称、类别/事件 ID、置信度、稳定帧和超时；
5. 保存草稿，确认无误后将生命周期改为“已发布”并保存；
6. 在“绑定到检测任务”区域选择任务并点击“绑定到任务”；
7. 进入“实时检测”，选择该任务；
8. 确认右侧出现“SOP 工序进度”，并能看到当前步骤和步骤状态；
9. 普通未绑定任务应继续只显示普通视觉检测内容，不显示 SOP 工序卡片。

## 7. 结论和后续建议

本次实施已经把 SOP 从“任务配置中的简单内嵌文本”提升为可制作、可发布、可绑定、可运行、可保存快照的独立业务能力，同时保持了综合视觉软件的非 SOP 任务兼容性。

建议下一阶段优先顺序为：

1. 把 SOP 定义目录从 JSON 迁移为带版本约束的数据库表；
2. 增加 OCR/条码/二维码条件编辑器和实时专属结果区；
3. 完成 SOP 历史详情、步骤时间轴和证据查询；
4. 补齐发布版本复制、返工、人工确认和审计；
5. 在真实算法运行环境补跑全量集成测试，再进行 PLC/TCP 周期协议联调。
