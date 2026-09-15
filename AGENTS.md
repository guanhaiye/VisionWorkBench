# VisionWorkbench 项目操作记忆

## 长期发布与更新规则

后续软件更新默认采用“全量安装包 + 增量更新包”的发布方式：

1. 全量安装包仅用于首次安装、重大版本升级、安装损坏恢复或增量包基线不匹配。
2. 普通版本更新只生成增量包，按发布清单和 SHA-256 只携带发生变化的程序文件。
3. 增量包默认不携带客户数据、数据库、配置、录制视频、自定义模型、Python/CUDA 运行时和海康 MVS Runtime。
4. 模型发生变化时单独生成模型组件包，只传输新增或哈希变化的模型；Python/CUDA/MVS 变化时单独生成运行时组件包。
5. 更新前停止软件并备份程序目录，更新后执行数据库迁移和环境自检；失败必须支持回滚，客户数据不能被覆盖。
6. 每次发布保留全量安装包作为恢复方案，并报告增量包与全量包的路径、大小、SHA-256、提交号和干净环境测试结果。

处理本仓库时遵守以下固定流程，避免每次重新排查：

## 拉取与编译

1. 主仓库目录：`E:\AI执行软件\VisionWorkbench`。
2. Gitee 主分支：`origin/main`。
3. 使用项目自带的 .NET 10 SDK：`.dotnet-sdk-10b\dotnet.exe`，不要使用系统自带的 .NET 9 SDK。
4. 拉取新代码后，若新增项目或缺少 `project.assets.json`，先执行：
   - `.dotnet-sdk-10b\dotnet.exe restore src\VisionWorkbench.App\VisionWorkbench.App.csproj --ignore-failed-sources -p:NuGetAudit=false`
5. 编译命令：
   - `.dotnet-sdk-10b\dotnet.exe build src\VisionWorkbench.App\VisionWorkbench.App.csproj --no-restore`
6. 若输出 DLL 被占用，先精确关闭现有 `VisionWorkbench` 进程，再重新编译；不要启动旧二进制。

## 启动与验证

1. 最新调试版程序：`src\VisionWorkbench.App\bin\Debug\net10.0-windows\VisionWorkbench.exe`。
2. 必须使用正常用户桌面权限启动。受限环境启动会导致 SQLite 报错：`attempt to write a readonly database`，表现为进程存在但主窗口不可见。
3. 启动后不能只检查进程，必须同时确认：
   - `HasExited` 为 `False`；
   - `MainWindowTitle` 为 `VisionWorkbench — AI 视觉工作台`；
   - `MainWindowHandle` 非零。
4. 如果进程存在但用户看不到窗口，优先检查：
   - `src\VisionWorkbench.App\bin\Debug\net10.0-windows\startup-error.log`；
   - 是否因权限导致参数数据库只读；
   - 是否启动在不可见或受限会话中。

## 提交与推送

1. 每次代码或项目说明修改完成后，先做与改动风险相称的验证。
2. 只提交本次相关文件，不覆盖用户的其他未提交改动。
3. 验证通过后自动提交并推送到 Gitee 的 `origin/main`，无需等待用户再次提醒。
4. 最终回复中报告编译结果、提交号和推送状态。
