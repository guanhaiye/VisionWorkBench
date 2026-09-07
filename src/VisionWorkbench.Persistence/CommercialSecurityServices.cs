using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace VisionWorkbench.Persistence;

/// <summary>方案 §8.4：统一追加式审计。普通业务代码只能新增事件，不能通过该服务修改或删除历史。</summary>
public sealed class AuditService(VisionDbContextFactory factory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<AuditEventEntity> RecordAsync(
        string action,
        string objectType,
        string? objectId,
        string actorName,
        string result = "success",
        string? detailsJson = null,
        long? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = factory.CreateDbContext();
            var previous = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(item => item.Id)
                .Select(item => item.Hash)
                .FirstOrDefaultAsync(cancellationToken);
            var item = new AuditEventEntity
            {
                ActorUserId = actorUserId,
                ActorName = actorName.Trim(),
                Action = action.Trim(),
                ObjectType = objectType.Trim(),
                ObjectId = objectId,
                Result = string.IsNullOrWhiteSpace(result) ? "success" : result.Trim(),
                DetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson,
                PreviousHash = previous,
                OccurredAtUtc = DateTime.UtcNow,
            };
            db.AuditEvents.Add(item);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            // SQLite 的 DateTime 转换可能调整 Kind/精度；先让 provider 完成往返，再计算链哈希。
            item.Hash = ComputeHash(item);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return item;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var previous = (string?)null;
        var events = await db.AuditEvents.AsNoTracking().OrderBy(item => item.Id).ToListAsync(cancellationToken);
        foreach (var item in events)
        {
            if (!string.Equals(item.PreviousHash, previous, StringComparison.Ordinal)
                || !string.Equals(item.Hash, ComputeHash(item), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            previous = item.Hash;
        }
        return true;
    }

    public async Task<IReadOnlyList<AuditEventEntity>> QueryAsync(
        DateTime? fromUtc = null, DateTime? toUtc = null, string? actorName = null,
        string? action = null, string? objectType = null, string? result = null,
        int limit = 1000, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var query = db.AuditEvents.AsNoTracking().AsQueryable();
        if (fromUtc is not null) query = query.Where(x => x.OccurredAtUtc >= fromUtc.Value);
        if (toUtc is not null) query = query.Where(x => x.OccurredAtUtc < toUtc.Value);
        if (!string.IsNullOrWhiteSpace(actorName)) query = query.Where(x => x.ActorName == actorName);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
        if (!string.IsNullOrWhiteSpace(objectType)) query = query.Where(x => x.ObjectType == objectType);
        if (!string.IsNullOrWhiteSpace(result)) query = query.Where(x => x.Result == result);
        return await query.OrderByDescending(x => x.Id).Take(Math.Clamp(limit, 1, 10000)).ToListAsync(cancellationToken);
    }

    private static string ComputeHash(AuditEventEntity item)
    {
        var value = string.Join("|", item.EventId, item.ActorUserId?.ToString() ?? "", item.ActorName,
            item.Action, item.ObjectType, item.ObjectId ?? "", item.Result, item.DetailsJson,
            item.OccurredAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), item.PreviousHash ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>方案 §8：服务层 RBAC，页面隐藏按钮不能替代此处的权限检查。</summary>
public sealed class AccessControlService(VisionDbContextFactory factory)
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);

    public static IReadOnlyList<(string Code, string Name)> PermissionCatalog { get; } =
    [
        ("production.run", "运行生产"), ("production.reset", "生产复位"),
        ("history.view", "查看历史"), ("history.export", "导出历史"), ("review.correct", "人工纠错"),
        ("recipe.edit", "编辑配方"), ("recipe.publish", "发布配方"), ("recipe.rollback", "回滚配方"),
        ("model.manage", "管理模型"), ("dataset.manage", "管理数据集"),
        ("device.configure", "配置设备"), ("communication.configure", "配置通信"),
        ("user.manage", "管理用户"), ("backup.create", "创建备份"), ("backup.restore", "恢复备份"),
        ("audit.view", "查看审计"), ("license.manage", "管理授权"), ("system.update", "系统升级")
    ];

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await SeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = factory.CreateDbContext();
            var roleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operator"] = "操作员", ["quality"] = "质量员", ["engineer"] = "工程师",
                ["expert"] = "专家", ["admin"] = "管理员"
            };
            foreach (var pair in roleNames)
            {
                if (!await db.Roles.AnyAsync(role => role.Code == pair.Key, cancellationToken).ConfigureAwait(false))
                {
                    db.Roles.Add(new RoleEntity { Code = pair.Key, Name = pair.Value });
                }
            }
            foreach (var permission in PermissionCatalog)
            {
                if (!await db.Permissions.AnyAsync(item => item.Code == permission.Code, cancellationToken).ConfigureAwait(false))
                {
                    db.Permissions.Add(new PermissionEntity { Code = permission.Code, Name = permission.Name });
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var roles = await db.Roles.ToDictionaryAsync(item => item.Code, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
            var permissions = await db.Permissions.ToDictionaryAsync(item => item.Code, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
            var rolePermissionMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["operator"] = ["production.run", "history.view"],
                ["quality"] = ["production.run", "history.view", "history.export", "review.correct"],
                ["engineer"] = PermissionCatalog.Select(item => item.Code).Where(code => code is not "user.manage" and not "license.manage" and not "system.update").ToArray(),
                ["expert"] = PermissionCatalog.Select(item => item.Code).Where(code => code is not "user.manage" and not "license.manage").ToArray(),
                ["admin"] = PermissionCatalog.Select(item => item.Code).ToArray(),
            };
            foreach (var pair in rolePermissionMap)
            {
                foreach (var code in pair.Value)
                {
                    if (!roles.TryGetValue(pair.Key, out var role) || !permissions.TryGetValue(code, out var permission)) continue;
                    if (!await db.RolePermissions.AnyAsync(item => item.RoleId == role.Id && item.PermissionId == permission.Id, cancellationToken).ConfigureAwait(false))
                    {
                        db.RolePermissions.Add(new RolePermissionEntity { RoleId = role.Id, PermissionId = permission.Id });
                    }
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SeedGate.Release();
        }
    }

    public async Task<bool> HasPermissionAsync(string userName, string permissionCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(permissionCode)) return false;
        await using var db = factory.CreateDbContext();
        return await (from user in db.Users
                      join userRole in db.UserRoles on user.Id equals userRole.UserId
                      join rolePermission in db.RolePermissions on userRole.RoleId equals rolePermission.RoleId
                      join permission in db.Permissions on rolePermission.PermissionId equals permission.Id
                      where user.UserName == userName && user.IsEnabled && permission.Code == permissionCode
                      select user.Id).AnyAsync(cancellationToken);
    }

    public async Task RequirePermissionAsync(string userName, string permissionCode, CancellationToken cancellationToken = default)
    {
        if (!await HasPermissionAsync(userName, permissionCode, cancellationToken))
        {
            throw new UnauthorizedAccessException($"用户 {userName} 没有权限 {permissionCode}");
        }
    }
}

/// <summary>数据库完整性检查，升级、恢复和一键自检共用。</summary>
public sealed class DatabaseIntegrityService(VisionDbContextFactory factory)
{
    public async Task<(bool IsHealthy, string Message)> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "";
        return (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result);
    }
}
