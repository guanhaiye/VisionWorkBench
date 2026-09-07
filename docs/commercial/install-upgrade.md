# 安装、升级与回滚手册

使用自包含 win-x64 发布目录或 Inno Setup 脚本安装。升级前停止生产任务、执行 `.vwbackup` 完整备份并校验 SHA-256；迁移失败时保留旧版本和数据，使用备份回滚。卸载默认保留 ProgramData/LocalAppData 数据，彻底删除必须二次确认。
