# VisionWorkbench 统一安装器实施方案

## 1. 项目目标

为 VisionWorkbench 制作一个统一的 Windows x64 商用安装程序，最终交付客户一个文件：

```text
VisionWorkbench-Setup-x64.exe
```

最终安装包固定输出到：

```text
E:\AI执行软件安装包
```

客户双击安装后，不需要手动安装或配置 .NET、Python、PyTorch、OpenCV、Ultralytics、AI Worker 依赖和基础模型运行环境。安装完成后可以直接启动软件、激活许可证并使用相应功能。

安装包不得包含开发数据集、测试数据集、客户数据、训练临时文件和开发缓存。

## 2. 核心交付要求

统一安装器必须实现：

- 自动安装 VisionWorkbench 主程序。
- 内置 .NET 10 自包含运行环境。
- 自动部署独立、固定版本、可迁移的 Python x64 运行环境。
- 自动部署 PyTorch、OpenCV、Ultralytics 及正式 AI Worker 所需依赖。
- 自动部署经过授权、允许商用及再分发的基础模型。
- 自动检测 NVIDIA 显卡、驱动和 Torch CUDA 可用状态。
- NVIDIA 环境满足要求时自动使用 GPU，否则自动使用 CPU。
- 不要求客户安装完整 CUDA Toolkit。
- NVIDIA 驱动过旧时给出明确中文提示，但不阻止使用 CPU 模式。
- 自动创建参数、数据库、日志、结果和备份目录。
- 支持覆盖升级，并保留客户参数、许可证、数据库和备份。
- 安装完成后执行环境自检并自动启动软件。
- 提供桌面快捷方式、开始菜单快捷方式和完整卸载功能。

## 3. 当前基础与需要完善的部分

仓库已经包含：

```text
scripts\build-commercial.ps1
installer\VisionWorkbench.iss
```

现有商用构建脚本已经采用：

```text
Release
win-x64
self-contained
```

这意味着客户不需要额外安装 .NET 10。

现有 Inno Setup 脚本已经具备主程序安装、桌面快捷方式、开始菜单、卸载程序和 `C:\ProgramData\VisionWorkbench` 可写目录创建能力。

仍需解决：

- 商用发布脚本尚未完整封装 Python 和 AI Worker 运行环境。
- 开发机 `.venv` 不适合直接复制给客户。
- 部分模型被 `.gitignore` 排除，不能依赖源码仓库自动提供。
- 海康工业相机依赖厂商 MVS Runtime。
- 安装程序版本号目前不应继续固定写死。
- 缺少统一的安装后环境自检。
- 缺少干净电脑上的完整安装、升级和卸载验证。

## 4. 主程序发布

继续使用 .NET 自包含发布：

```powershell
dotnet publish src\VisionWorkbench.App\VisionWorkbench.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true
```

发布结果进入临时交付目录，然后由 Inno Setup 打包。

主程序安装到：

```text
C:\Program Files\VisionWorkbench
```

安装目录只存放程序、只读资源、AI Worker、Python运行环境和基础模型，不存放运行期间需要修改的客户参数。

## 5. Python环境封装

不得直接复制开发电脑中的 `.venv`，也不得在客户电脑安装过程中联网执行 `pip install`。

应建立可迁移的独立运行环境：

```text
runtime\
  python\
    python.exe
    python312.dll
    python3.dll
    Lib\
    site-packages\
```

实施要求：

- 使用固定版本的 Python x64。
- 使用离线 wheel 仓库构建依赖。
- 锁定所有 Python 包版本。
- 生成依赖版本和许可证清单。
- 删除测试文件、下载缓存、`.pytest_cache` 和 `__pycache__`。
- 不读取客户电脑上的系统 Python。
- 不依赖 PATH、PYTHONPATH 等全局环境变量。
- 软件始终优先使用 `{app}\runtime\python\python.exe`。

建议增加：

```text
installer\python-lock.txt
installer\wheelhouse\
```

如果离线 wheel 文件体积过大，可由受控构建机生成 Python Runtime 成品包；不建议把全部第三方二进制直接提交到普通源码仓库。

## 6. PyTorch、CUDA与显卡适配

### 6.1 统一安装器策略

统一安装器内置一套经过验证的 CUDA 版 PyTorch 环境。该环境必须同时支持：

- 有兼容 NVIDIA 显卡时使用 GPU。
- 没有 NVIDIA 显卡时使用 CPU。
- NVIDIA 驱动不兼容时回退 CPU。
- Intel 或 AMD 显卡环境下使用 CPU。

运行时判断：

```python
import torch

device = "cuda" if torch.cuda.is_available() else "cpu"
```

### 6.2 CUDA边界

- PyTorch CUDA 发行包携带其需要的的 CUDA 用户态运行库。
- 客户不需要安装完整 CUDA Toolkit。
- 客户仍需拥有兼容的 NVIDIA 显卡驱动。
- 安装器不应静默强制安装或升级 NVIDIA 驱动。
- 检测到驱动过旧时应显示检测值、最低要求和升级建议。
- 驱动异常不能导致整个软件无法启动，应自动回退 CPU。
- TensorRT 是可选加速组件，不得成为软件启动的必要条件。

### 6.3 启动检测结果

软件应记录：

- Torch版本。
- GPU名称。
- GPU显存。
- NVIDIA驱动版本。
- CUDA是否可用。
- 最终使用的执行设备。
- 回退 CPU 的原因。

检测结果写入：

```text
C:\ProgramData\VisionWorkbench\logs\environment-check.log
```

## 7. AI Worker封装

至少核对并封装正式功能使用的 Worker：

```text
workers\atu5
workers\yolo11
workers\yoloe
workers\sam1
workers\paddleocr-vl
```

根据实际产品功能继续检查其他 Worker，不能机械地把整个开发目录复制进安装包。

不得包含：

```text
.venv
.pytest_cache
__pycache__
.pip-download
训练临时文件
测试样例
用户数据集
用户模型
下载缓存
```

安装状态下 Worker 根目录固定为：

```text
{app}\workers
```

开发状态可以继续支持向上查找仓库中的 `workers`，但发布状态不得依赖源码目录。

## 8. 基础模型管理

只允许打包确认可以商用和再分发的基础模型。

建立模型清单：

```text
installer\model-manifest.json
```

每个模型至少记录：

- 模型文件名。
- 模型类型。
- 模型版本。
- SHA-256。
- 官方来源。
- 许可证名称。
- 是否允许商用。
- 是否允许再分发。
- 安装目标位置。

以下内容不得进入安装包：

- 客户数据集。
- 客户训练生成的模型。
- 开发人员的私人模型。
- 来源和许可不明确的模型。

无法合法再分发的模型应由用户安装后导入，不能为了方便擅自打包。

## 9. 海康工业相机环境

VisionWorkbench 海康相机模块依赖 MVS Runtime 和 `MvCameraControl.dll`。

安装与运行要求：

- 自动检测客户电脑是否安装 MVS Runtime。
- 已安装时自动启用海康相机 Provider。
- 未安装时软件仍可使用文件输入、普通USB相机和其他功能。
- 未安装时显示清晰中文提示，不得崩溃。
- 只有确认海康厂商允许再分发后，才能将 MVS Runtime 合并进统一安装器。
- 未确认再分发授权前，不得直接打包厂商 DLL。

可以在安装器自定义组件中显示“工业相机支持”，但不能让其影响主程序安装。

## 10. 参数、许可证与运行数据

统一存放在：

```text
C:\ProgramData\VisionWorkbench
```

建议目录：

```text
C:\ProgramData\VisionWorkbench\
  Config\
  backups\
  logs\
  results\
  sop-recordings\
  temp-images\
```

升级安装必须保留：

- `settings.json`。
- `visionworkbench.db`。
- TCP/IP通讯配置。
- SOP定义及配置。
- 任务配置。
- 相机参数。
- 用户设置。
- 许可证。
- 参数备份。
- 日志和结果。
- 客户模型路径配置。

卸载时默认保留客户参数和许可证。可增加以下可选项，但默认不能勾选：

```text
同时删除参数、许可证和运行数据
```

覆盖升级前建议自动创建一次参数备份。

## 11. 安装器交互

安装器继续使用 Inno Setup，并提供中文界面。

普通客户只显示：

```text
推荐安装
自定义安装
```

不要向普通客户暴露 Torch、CUDA、Python 等技术选项。

自定义安装可以选择：

- 基础模型。
- OCR组件。
- 智能标注组件。
- 工业相机支持。
- 桌面快捷方式。

CPU/GPU模式由软件自动检测，客户无需手动选择。

安装器需要具备：

- Windows x64检测。
- 磁盘空间检测。
- 旧版本检测。
- 覆盖升级。
- 中文许可协议。
- 软件图标和发布者信息。
- 桌面快捷方式。
- 开始菜单快捷方式。
- 安装日志。
- 静默安装参数。
- 安装完成后环境自检。
- 安装完成后启动软件。
- 完整卸载功能。

## 12. 环境自检程序

为主程序增加命令行入口：

```text
VisionWorkbench.exe --environment-check
```

也可以实现独立的轻量检查工具，但不得额外要求客户操作。

检查内容：

- `C:\ProgramData\VisionWorkbench` 是否可写。
- 参数目录能否创建。
- SQLite数据库能否创建、打开和升级。
- Python能否启动。
- `torch`能否导入。
- `cv2`能否导入。
- Ultralytics能否导入。
- Worker脚本是否完整。
- 基础模型是否存在并通过 SHA-256校验。
- CUDA是否可用。
- GPU和驱动信息是否可读取。
- 海康 MVS Runtime是否存在。
- 许可证模块能否初始化。

全部通过返回退出码 `0`，必要组件失败返回非零退出码。

安装完成页只显示简洁结果：

```text
环境检查通过
当前运行设备：NVIDIA GPU
```

或：

```text
环境检查通过
当前运行设备：CPU
```

失败时提供“查看诊断信息”，打开自检日志。

## 13. 统一构建脚本

增加：

```text
scripts\build-installer.ps1
```

建议参数：

```powershell
param(
    [string]$Version,
    [string]$OutputDirectory = 'E:\AI执行软件安装包',
    [string]$SigningCertificate,
    [switch]$SkipTests,
    [switch]$Unsigned
)
```

执行流程：

1. 检查工作区状态和版本参数。
2. 使用仓库规定的本地 .NET 10 SDK。
3. 还原 .NET依赖。
4. 执行 Release编译。
5. 执行非UI自动化测试。
6. 自包含发布 WPF主程序。
7. 构建或复制固定 Python运行环境。
8. 复制正式 AI Worker。
9. 复制确认可分发的基础模型。
10. 删除缓存、测试文件和开发环境。
11. 生成模型及发布文件清单。
12. 对程序文件执行代码签名。
13. 调用 Inno Setup生成统一安装器。
14. 对最终安装包执行代码签名。
15. 生成安装包 SHA-256。
16. 在临时路径执行静默安装和环境自检。
17. 将最终产物复制到指定输出目录。

版本号必须从统一项目版本或构建参数读取，不能继续固定为 `1.0.0`。

## 14. 最终输出

最终目录：

```text
E:\AI执行软件安装包\
  VisionWorkbench-Setup-x64.exe
  VisionWorkbench-Setup-x64.exe.sha256
  release-manifest.json
  安装说明.txt
```

`release-manifest.json` 至少包含：

- 产品名称。
- 产品版本。
- 构建时间。
- Git提交号。
- Windows运行架构。
- Python版本。
- Torch版本。
- CUDA运行库版本。
- Worker版本或哈希。
- 模型文件和哈希。
- 安装包哈希。
- 是否进行了代码签名。

## 15. 数字签名与敏感信息

支持对以下文件签名：

- `VisionWorkbench.exe`。
- 其他需要直接执行的内部 EXE。
- 最终 `VisionWorkbench-Setup-x64.exe`。

没有正式证书时可以生成内部测试包，但构建日志必须明确提示：

```text
警告：当前安装包未进行代码签名
```

以下内容不得提交或打包：

- 代码签名证书私钥。
- 许可证签发私钥。
- Gitee Token。
- 密码。
- 客户许可证。
- 客户数据。

## 16. 干净环境验收

必须在没有开发环境的干净 Windows 电脑或虚拟机中验证。

### 16.1 基础安装

- 未安装 .NET 时可以安装并启动。
- 未安装 Python 时可以安装并运行 AI Worker。
- 未安装 CUDA Toolkit 时可以正常运行。
- 安装路径带空格时可以运行。
- 普通用户启动时不会出现数据库只读错误。

### 16.2 CPU/GPU

- 没有 NVIDIA显卡时自动使用 CPU。
- 有兼容 NVIDIA显卡时自动使用 GPU。
- NVIDIA驱动过旧时显示提示并回退 CPU。
- Intel或AMD显卡电脑可以使用 CPU模式启动。

### 16.3 软件功能

- 参数可以保存，重启后仍然存在。
- 许可证可以正常激活和校验。
- 数据库可以初始化和升级。
- 任务配置可用。
- 实时检测可用。
- SOP创建、任务关联和实时执行可用。
- 参数备份和恢复可用。
- USB相机可以识别。
- 未安装 MVS Runtime时软件不崩溃。
- 安装 MVS Runtime后海康相机可以识别。

### 16.4 升级与卸载

- 覆盖升级前自动备份参数。
- 覆盖升级后参数不丢失。
- 覆盖升级后许可证不丢失。
- 覆盖升级后数据库可正常迁移。
- 卸载默认保留参数和许可证。
- 用户主动选择时可以彻底清除运行数据。

## 17. 完成标准

只有同时满足以下条件才能报告完成：

- 已生成唯一统一安装包。
- 安装包输出到 `E:\AI执行软件安装包`。
- 客户不需要使用命令行。
- 客户不需要安装 .NET、Python或Torch。
- 软件能够自动选择 GPU或CPU。
- 驱动不兼容时能安全回退。
- 干净电脑安装测试通过。
- 覆盖升级测试通过。
- 安装包不包含数据集和开发缓存。
- 安装包、文件清单及 SHA-256齐全。
- 编译与自动化测试通过。
- 修改已提交并推送到 Gitee。
- 最终报告包含提交号、安装包路径、文件大小、SHA-256和验收结果。

## 18. Codex执行要求

执行本方案时不要只分析，应直接实现、构建和验证。

必须遵守：

- 先检查现有脚本和实现，复用已有商用发布基础。
- 不覆盖其他线程或用户尚未提交的修改。
- 不把数据集、客户文件和密钥加入提交。
- 写入 `E:\AI执行软件安装包` 若需要额外文件系统权限，应正常申请权限，不能擅自更换输出位置。
- 遇到依赖许可证或厂商再分发授权不明确时，先保留检测和提示机制，不得违规打包。
- 修改完成后执行构建、测试、安装、自检和升级验证。
- 每次有效代码修改完成后，按项目约定提交并推送 Gitee。

执行完成后报告：

```text
Git提交号：
Gitee推送状态：
安装包路径：
安装包大小：
SHA-256：
数字签名状态：
CPU环境测试：
NVIDIA GPU环境测试：
覆盖升级测试：
已知限制：
```
