# VisionWorkbench

Windows 本地 AI 视觉工作台。第一期交付**完整纵向闭环骨架**：

```
相机/图片目录/视频文件 → WPF 实时预览 + 检测框叠加
        ↓ 帧调度（LatestOnly 实时丢旧帧 / Bounded 逐帧不漏）
Python Worker（进程隔离，JSON Lines 协议）
        ↓ AlgorithmOutput（归一化坐标检测框）
规则引擎判定 OK / NG / 待复核
        ↓
SQLite 六表落库（批次/记录/计数事件/视觉事件/纠错审计）
        ↓
历史分页查询 + 证据图查看 + 人工纠错（原因必填，全程审计）
```

算法以插件形式外置：换算法 = 换 Worker 内核，宿主（.NET 10 WPF）零改动。

## 工程结构

| 目录 | 说明 |
|---|---|
| `src/VisionWorkbench.Contracts` | 协议消息、AlgorithmOutput 系、PluginManifest、标准错误码 |
| `src/VisionWorkbench.Domain` | 规则引擎（数量/类别/置信度）、计数累计与人工修正审计、ROI 过滤 |
| `src/VisionWorkbench.Cameras.Abstractions` | ICameraProvider/ICameraSession、VideoFrame（纯 BGR，不依赖 OpenCvSharp） |
| `src/VisionWorkbench.Cameras.Files` | 图片目录 / 视频文件虚拟相机（自动化测试回放基础） |
| `src/VisionWorkbench.Cameras.Usb` | USB 相机（DirectShow 索引探测） |
| `src/VisionWorkbench.Infrastructure` | WorkerProcess（JSON Lines 亲子进程管理）、PluginScanner、Serilog、临时图传图 |
| `src/VisionWorkbench.Algorithms` | 算法会话与管理器（每插件一进程、崩溃重启策略） |
| `src/VisionWorkbench.Persistence` | EF Core SQLite 六表 + 仓储（IDbContextFactory 短生命周期 Context，WAL） |
| `src/VisionWorkbench.Application` | FrameScheduler 帧调度、DetectionRunService 检测编排、配方/批次服务 |
| `src/VisionWorkbench.App` | WPF 六页：实时检测/任务配置/历史记录/算法管理/设备管理/系统设置 |
| `workers/python-sdk` | `vw_worker` 纯标准库 SDK：stdin 行循环、生命周期钩子、错误码封装 |
| `workers/sample-counter` | 示例插件：OpenCV 阈值+形态学+轮廓计数 |
| `tools/make_test_images.py` | 合成已知真值的测试图片集/视频 |
| `tests/VisionWorkbench.Tests` | xUnit：协议/规则/调度/持久化/Worker 真进程集成/服务层端到端 |

## 环境要求

- Windows 10/11，.NET 10 SDK
- Python 3.12（`py -3.12`）；项目 venv 位于 `workers/.venv`

## 构建与运行

```powershell
# 1. Python 环境（一次性）
py -3.12 -m venv workers\.venv
workers\.venv\Scripts\pip install -r workers\requirements.txt

# 2. 生成测试样本（可选，E2E 测试依赖 samples/ 已生成）
workers\.venv\Scripts\python tools\make_test_images.py

# 3. 构建全解
dotnet build VisionWorkbench.slnx

# 4. 运行
dotnet run --project src\VisionWorkbench.App

# 5. 测试（无 Python 环境时集成/E2E 自动跳过）
dotnet test tests\VisionWorkbench.Tests\VisionWorkbench.Tests.csproj

# 6. Python 插件单测
workers\.venv\Scripts\python -m pytest workers\sample-counter\tests -q
```

数据目录默认 `%LOCALAPPDATA%\VisionWorkbench`（数据库/日志/临时图/证据图），
可在 `settings.json` 或应用设置页修改，支持中文与空格路径。

## 关键设计落点

- **进程隔离**：算法 Worker 独立 Python 进程，崩溃不影响宿主；stdin/stdout JSON Lines，
  stderr 持续泵读到插件专属日志；correlationId 请求表匹配（容忍乱序响应）。
- **强制 UTF-8** 双侧编码，杜绝 GBK 乱码；Python 侧行缓冲 flush 防管道阻塞。
- **帧调度**：实时相机 LatestOnly（推理慢时丢旧帧并计数）；有限源（图片目录/视频）Bounded
  逐帧不漏。Stop 只阻止取新帧，在途推理必须完成。
- **落库不阻塞检测**：记录走后台写队列；队列生命周期独立于检测会话，
  停止/退出前排空（StopAsync 返回即可查询）。
- **证据策略**：OK 只存结构化结果；NG/待复核另存原图+标注图到 `evidence/yyyyMMdd/`。
- **审计**：计数每个变化都是事件（Accepted/Corrected/CounterReset…），人工修正必须填原因，
  纠错留 before/after 快照、操作人、时间。

## 已知限制（第一期）

- 重叠/粘连目标的轮廓会合并，计为 1（OpenCV 传统视觉内核固有；换深度学习 Worker 即解）。
- 动态去重/流水线跨线计数的**协议与数据表已就位**，算法内核下一期实现。
- USB 设备枚举只显示索引+探测分辨率，无友好设备名（DirectShow/MF 名称增强后续）。
- 角色切换免密（工程师凭证与审计下一期，CFG-002/003）。
- 帧传图走临时 PNG 文件（文档 §11.3 首版方案）；共享内存/零拷贝后续。
- 开发态 Python 解释器默认解析到 `workers/.venv`；打包态改为随插件分发的内嵌运行时
  （appsettings.json / 设置页可覆盖）。
- 缺陷检测/行为分析插件、CUDA 推理、工业相机 Provider、安装包（Inno Setup）均不在本期。

## 测试覆盖（对应测试方案编号）

协议序列化往返与未知字段容忍（PLG-014）、清单校验（PLG-002/003）、规则引擎全种类
（RUL-001~005）、ROI 边界策略（CNT-S-007）、计数与修正审计（CNT-S-010）、帧调度丢帧
（FRM-001/002/003）、SQLite 六表往返与分页（DAT-001/003）、Worker 真进程生命周期
（PLG-004/008/009/010/011）、服务层端到端（§24 冒烟 1~10 等价物）、插件 pytest。
