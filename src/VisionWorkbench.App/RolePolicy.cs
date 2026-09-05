namespace VisionWorkbench.App;

/// <summary>首版三档使用模式。认证接入前仍由本机配置切换，但页面能力边界统一由此处执行。</summary>
public static class RolePolicy
{
    public static string Current => AppServices.Instance.Settings.CurrentRole?.Trim().ToLowerInvariant() ?? "operator";
    public static bool CanEditRecipe => Current is "engineer" or "expert";
}
