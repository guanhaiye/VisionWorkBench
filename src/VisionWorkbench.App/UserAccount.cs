using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VisionWorkbench.App;

public sealed class UserAccount
{
    public string UserName { get; set; } = "";
    public string Role { get; set; } = "operator";
    public string? AvatarPath { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsSuperAdmin { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string RoleDisplay => AccountRules.RoleDisplayName(Role);

    [JsonIgnore]
    public string StatusDisplay => IsEnabled ? "启用" : "已停用";
}

public static class AccountRules
{
    public const string SuperAdminName = "17826809794";

    public static string NormalizeUserName(string? name) => name?.Trim() ?? "";

    public static bool ValidateUserName(string? name, out string message)
    {
        var normalized = NormalizeUserName(name);
        if (normalized.Length is < 3 or > 32)
        {
            message = "用户名长度必须为 3 到 32 个字符。";
            return false;
        }
        if (!Regex.IsMatch(normalized, "^[\\p{L}][\\p{L}\\p{N}_.-]{2,31}$"))
        {
            message = "用户名必须以字母或中文开头，只能包含字母、中文、数字、下划线、点或短横线。";
            return false;
        }

        message = "";
        return true;
    }

    public static bool ValidatePassword(string? password, out string message)
    {
        password ??= "";
        if (password.Length < 8)
        {
            message = "密码至少需要 8 位。";
            return false;
        }
        if (password.Any(char.IsWhiteSpace))
        {
            message = "密码不能包含空格。";
            return false;
        }
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            message = "密码必须同时包含字母和数字。";
            return false;
        }

        message = "";
        return true;
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash)) return false;

        if (storedHash.StartsWith("pbkdf2$", StringComparison.Ordinal))
        {
            var parts = storedHash.Split('$');
            if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations)) return false;
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // 兼容此前单用户版本保存的 SHA-256 密码摘要。
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(legacy.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(storedHash.Trim().ToUpperInvariant()));
    }

    public static string RoleDisplayName(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "superadmin" => "超级管理员",
        "expert" => "专家",
        "engineer" => "工程师",
        _ => "操作员",
    };
}
