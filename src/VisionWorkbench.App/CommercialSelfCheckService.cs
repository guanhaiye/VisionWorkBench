using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public sealed record SelfCheckItem(string Code, string State, string Summary, string Recommendation);

public sealed record SelfCheckReport(DateTime CheckedAtUtc, IReadOnlyList<SelfCheckItem> Items)
{
    [JsonIgnore]
    public string OverallState => Items.Any(item => item.State == "red") ? "red" :
        Items.Any(item => item.State == "yellow") ? "yellow" : "green";
}

/// <summary>方案 §16.1：现场一键自检；不上传数据，不在报告中包含密码、Token 或客户原图。</summary>
public static class CommercialSelfCheckService
{
    public static async Task<SelfCheckReport> RunAsync(AppServices services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var items = new List<SelfCheckItem>();
        var dataDir = services.Settings.DataDirectory;
        items.Add(CheckDirectory(dataDir));
        items.Add(CheckDisk(dataDir));

        var integrity = await services.DatabaseIntegrity.CheckAsync(cancellationToken);
        items.Add(new SelfCheckItem("DB-001", integrity.IsHealthy ? "green" : "red",
            integrity.IsHealthy ? "数据库完整性检查通过" : $"数据库完整性检查失败：{integrity.Message}",
            integrity.IsHealthy ? "" : "停止高风险操作并使用最近有效备份恢复。"));

        var auditHealthy = await services.Audit.VerifyChainAsync(cancellationToken);
        items.Add(new SelfCheckItem("AUDIT-001", auditHealthy ? "green" : "red",
            auditHealthy ? "审计链校验通过" : "审计链校验失败，可能存在篡改或异常中断",
            auditHealthy ? "" : "保留当前数据库和日志，联系管理员进行审计取证。"));

        var license = services.License.Current;
        items.Add(new SelfCheckItem("LIC-001", license.IsValid ? "green" : "red",
            license.Message, license.IsValid ? "" : "导入有效许可证后再使用受限生产功能。"));

        var plugins = services.AlgorithmManager.ScanPlugins().ToArray();
        var invalidPlugins = plugins.Count(item => item.Status.ToString() is not "Valid");
        items.Add(new SelfCheckItem("ALG-001", invalidPlugins == 0 ? "green" : "yellow",
            invalidPlugins == 0 ? $"插件检查通过，共 {plugins.Length} 个插件" : $"发现 {invalidPlugins} 个插件异常，共 {plugins.Length} 个插件",
            invalidPlugins == 0 ? "" : "打开插件管理，修复缺少 manifest 或依赖的插件。"));

        return new SelfCheckReport(DateTime.UtcNow, items);
    }

    private static SelfCheckItem CheckDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".self-check-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new SelfCheckItem("DISK-001", "green", "数据目录可读写", "");
        }
        catch (Exception ex)
        {
            return new SelfCheckItem("DISK-001", "red", $"数据目录不可写：{ex.Message}", "检查目录权限并确保磁盘未被策略软件锁定。");
        }
    }

    private static SelfCheckItem CheckDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return new SelfCheckItem("DISK-002", "yellow", "无法识别数据盘", "检查数据目录配置。");
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
            return new SelfCheckItem("DISK-002", freeGb >= 5 ? "green" : "yellow",
                $"数据盘剩余空间 {freeGb:0.0} GB", freeGb >= 5 ? "" : "清理安全可删除的缓存或扩容数据盘。");
        }
        catch (Exception ex)
        {
            return new SelfCheckItem("DISK-002", "yellow", $"磁盘空间检查失败：{ex.Message}", "手动检查数据盘剩余空间。");
        }
    }
}
