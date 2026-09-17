namespace VisionWorkbench.App;

/// <summary>首版三档使用模式。认证接入前仍由本机配置切换，但页面能力边界统一由此处执行。</summary>
public static class RolePolicy
{
    public static string Current => AppServices.Instance.Settings.CurrentRole?.Trim().ToLowerInvariant() ?? "operator";
    // 超级管理员在数据库中统一使用 admin 角色，必须拥有完整的配置编辑权限。
    public static bool CanEditRecipe => Current is "admin" or "engineer" or "expert";
}
