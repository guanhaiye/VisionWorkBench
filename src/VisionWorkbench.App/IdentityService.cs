using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public sealed record AuthenticationResult(bool Succeeded, UserEntity? User, string Reason, string? EffectiveRole = null, bool IsAdministrator = false);

/// <summary>方案 §8.3：数据库账号、失败计数和锁定；UI 仅负责收集凭据，不负责安全策略。</summary>
public sealed class IdentityService(VisionDbContextFactory factory, AuditService audit)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<AuthenticationResult> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var normalized = AccountRules.NormalizeUserName(userName);
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleOrDefaultAsync(item => item.UserName == normalized, cancellationToken);
        if (user is null)
        {
            await RecordLoginAsync(db, null, normalized, false, "user_not_found", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AuthenticationResult(false, null, "用户名或密码错误");
        }
        var now = DateTime.UtcNow;
        if (!user.IsEnabled)
        {
            await RecordLoginAsync(db, user.Id, normalized, false, "disabled", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AuthenticationResult(false, user, "用户已停用");
        }
        if (user.LockoutUntilUtc is not null && user.LockoutUntilUtc > now)
        {
            await RecordLoginAsync(db, user.Id, normalized, false, "locked", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AuthenticationResult(false, user, $"登录失败次数过多，请于 {user.LockoutUntilUtc:HH:mm} 后重试");
        }
        if (!AccountRules.VerifyPassword(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailures)
            {
                user.LockoutUntilUtc = now.Add(LockoutDuration);
                user.FailedLoginCount = 0;
            }
            user.UpdatedAtUtc = now;
            await RecordLoginAsync(db, user.Id, normalized, false, "invalid_credentials", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new AuthenticationResult(false, user, "用户名或密码错误");
        }

        user.FailedLoginCount = 0;
        user.LockoutUntilUtc = null;
        user.LastLoginAtUtc = now;
        if (!user.PasswordHash.StartsWith("pbkdf2$", StringComparison.Ordinal))
            user.PasswordHash = AccountRules.HashPassword(password);
        user.UpdatedAtUtc = now;
        await RecordLoginAsync(db, user.Id, normalized, true, "success", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("auth.login", "user", normalized, normalized, detailsJson: "{\"source\":\"identity\"}",
            actorUserId: user.Id, cancellationToken: cancellationToken);
        return new AuthenticationResult(true, user, "登录成功");
    }

    public async Task<(string Role, bool IsAdministrator)> GetEffectiveRoleAsync(string userName, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var role = await (from user in db.Users
                          join userRole in db.UserRoles on user.Id equals userRole.UserId
                          join roleDb in db.Roles on userRole.RoleId equals roleDb.Id
                          where user.UserName == userName && user.IsEnabled
                          select roleDb.Code).FirstOrDefaultAsync(cancellationToken) ?? "operator";
        return (role, string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAccountAsync(UserAccount account, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleOrDefaultAsync(item => item.UserName == account.UserName, cancellationToken);
        if (user is null)
        {
            user = new UserEntity { UserName = account.UserName.Trim(), CreatedAtUtc = account.CreatedAt.ToUniversalTime() };
            db.Users.Add(user);
        }
        user.PasswordHash = account.PasswordHash;
        user.IsEnabled = account.IsEnabled;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var roleCode = account.IsSuperAdmin ? "admin" : account.Role.Trim().ToLowerInvariant();
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Code == roleCode, cancellationToken)
            ?? await db.Roles.FirstAsync(item => item.Code == "operator", cancellationToken);
        if (!await db.UserRoles.AnyAsync(item => item.UserId == user.Id && item.RoleId == role.Id, cancellationToken))
        {
            db.UserRoles.Add(new UserRoleEntity { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteAccountAsync(string userName, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleOrDefaultAsync(item => item.UserName == userName, cancellationToken);
        if (user is null) return;
        db.UserRoles.RemoveRange(db.UserRoles.Where(item => item.UserId == user.Id));
        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("user.delete", "user", userName, userName, detailsJson: "{\"source\":\"user_management\"}",
            cancellationToken: cancellationToken);
    }

    private static async Task RecordLoginAsync(VisionDbContext db, long? userId, string userName, bool succeeded,
        string reason, CancellationToken cancellationToken)
    {
        db.LoginEvents.Add(new LoginEventEntity
        {
            UserId = userId, UserName = userName, Succeeded = succeeded, Reason = reason,
            OccurredAtUtc = DateTime.UtcNow,
        });
        await Task.CompletedTask;
    }
}
