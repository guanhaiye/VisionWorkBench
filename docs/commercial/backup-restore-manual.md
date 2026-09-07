# 数据备份与恢复手册

`.vwbackup` 包含 `manifest.json`、`database/visionworkbench.db`、settings、通信配置、配方/模型元数据和校验信息。恢复仅接受安全相对路径、先校验清单和 SQLite 完整性，再替换数据库；运行任务时禁止恢复。可选 AES-256-GCM 密钥必须独立保管。
