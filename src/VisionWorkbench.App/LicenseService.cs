using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public sealed record LicensePayload(
    string LicenseId,
    string Customer,
    string Product,
    string Edition,
    DateTime NotBeforeUtc,
    DateTime ExpiresAtUtc,
    string MachineFingerprint,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, int>? Limits = null);

public sealed record LicenseDocument(LicensePayload Payload, string Signature, string SignatureAlgorithm = "ECDSA_P256_SHA256");

public sealed record LicenseStatus(
    bool IsValid,
    bool IsDevelopment,
    string State,
    string Message,
    LicensePayload? Payload,
    DateTime CheckedAtUtc);

public sealed record LicenseActivationRequest(
    string Product,
    string MachineFingerprint,
    string RequestedAtUtc,
    string RequestId);

/// <summary>客户端只持有公钥，离线校验签名和设备绑定；正式运行必须提供有效许可证。</summary>
public sealed class LicenseService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _licensePath;
    private readonly string _clockStatePath;
    private readonly string _clockBackupStatePath;
    private readonly string _clockTopologyMarkerPath;
    private readonly string _legacyClockStatePath;
    private readonly string? _publicKeyBase64;
    private readonly VisionDbContextFactory? _database;
    private readonly DateTime _processStartUtc = DateTime.UtcNow;
    private readonly long _processStartTimestamp = Stopwatch.GetTimestamp();
    private readonly System.Threading.Timer _backgroundValidationTimer;
    private readonly object _validationGate = new();
    private LicenseStatus? _cached;

    private const string ClockRegistryPath = @"Software\VisionWorkbench\License";
    private const string ClockRegistryValue = "ProtectedClock";
    private const string ClockTopologyRegistryValue = "ProtectedClockTopology";

    public LicenseService(string dataDirectory, string? publicKeyBase64 = null, VisionDbContextFactory? database = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _licensePath = Path.Combine(dataDirectory, "license.json");
        _clockStatePath = Path.Combine(dataDirectory, "license-clock.dat");
        var localClockDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionWorkbench");
        Directory.CreateDirectory(localClockDirectory);
        _clockBackupStatePath = Path.Combine(localClockDirectory, "license-clock.dat");
        _clockTopologyMarkerPath = Path.Combine(dataDirectory, "license-clock.v2");
        _legacyClockStatePath = Path.Combine(dataDirectory, "license-clock.json");
        _publicKeyBase64 = publicKeyBase64;
        _database = database;
        _backgroundValidationTimer = new System.Threading.Timer(
            _ => RunBackgroundValidation(),
            state: null,
            dueTime: TimeSpan.FromMinutes(10),
            period: TimeSpan.FromMinutes(10));
        SystemEvents.TimeChanged += OnSystemTimeChanged;
    }

    public string LicensePath => _licensePath;
    public LicenseStatus Current
    {
        get
        {
            var now = DateTime.UtcNow;
            if (_cached is null || now < _cached.CheckedAtUtc ||
                now - _cached.CheckedAtUtc >= TimeSpan.FromMinutes(1))
            {
                return Validate();
            }
            return _cached;
        }
    }

    public LicenseStatus Validate()
    {
        lock (_validationGate)
        {
            return ValidateCore();
        }
    }

    private LicenseStatus ValidateCore()
    {
        var now = DateTime.UtcNow;
        var monotonicNow = _processStartUtc + Stopwatch.GetElapsedTime(_processStartTimestamp);
        if (now < monotonicNow.AddMinutes(-5))
        {
            return _cached = Invalid("检测到运行期间系统时间回拨，许可证暂时受限", now);
        }
        if (!File.Exists(_licensePath))
        {
            SaveClock(now);
            return _cached = new LicenseStatus(false, false, "missing", "缺少许可证，请导入有效许可证", null, now);
        }
        try
        {
            var document = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(_licensePath), JsonOptions)
                ?? throw new InvalidDataException("许可证为空");
            var payload = document.Payload;
            if (!string.Equals(document.SignatureAlgorithm, "ECDSA_P256_SHA256", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("不支持的许可证签名算法");
            if (string.IsNullOrWhiteSpace(_publicKeyBase64))
                return _cached = Invalid("客户端未配置许可证公钥", now);
            if (!Verify(document)) return _cached = Invalid("许可证签名校验失败", now);
            if (!string.Equals(payload.Product, "VisionWorkbench", StringComparison.OrdinalIgnoreCase))
                return _cached = Invalid("许可证产品不匹配", now);
            if (!string.Equals(payload.MachineFingerprint, MachineFingerprint(), StringComparison.OrdinalIgnoreCase))
                return _cached = Invalid("许可证未绑定当前设备", now);
            var last = ReadClock();
            if (last is null && HasProtectedClockState())
                return _cached = Invalid("本机许可证时间状态无效，请重新导入许可证", now);
            if (last is not null && now < last.Value.AddMinutes(-5))
                return _cached = Invalid("检测到系统时间回拨，许可证暂时受限", now);
            SaveClock(now);
            if (now < payload.NotBeforeUtc) return _cached = Invalid("许可证尚未生效", now, payload);
            if (now > payload.ExpiresAtUtc) return _cached = Invalid("许可证已过期", now, payload);
            return _cached = new LicenseStatus(true, false, "valid", $"许可证有效期至 {payload.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", payload, now);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or CryptographicException or InvalidDataException)
        {
            return _cached = Invalid($"许可证无效：{ex.Message}", now);
        }
    }

    private void RunBackgroundValidation()
    {
        try
        {
            // 后台校验不直接操作界面，只更新 Current；界面和功能调用会读取最新状态。
            Validate();
        }
        catch
        {
            // 后台线程不能让主程序因校验异常退出；下一轮定时校验会继续执行。
        }
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e) => RunBackgroundValidation();

    public async Task<LicenseStatus> ImportAsync(string licenseFile, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(licenseFile);
        if (!File.Exists(source)) throw new FileNotFoundException("许可证文件不存在", source);
        if (string.Equals(source, _licensePath, StringComparison.OrdinalIgnoreCase))
        {
            _cached = null;
            var current = Validate();
            await RecordEventAsync(current, "import", cancellationToken);
            if (!current.IsValid || current.IsDevelopment) throw new InvalidDataException(current.Message);
            return current;
        }
        var temp = _licensePath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            // 先完整复制并释放源文件和临时文件句柄，再替换正式许可证。
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using (var input = File.Open(source, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    await using (var output = File.Create(temp))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                    }
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(150, cancellationToken);
                }
            }

            IOException? lastIoException = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(temp, _licensePath, overwrite: true);
                    lastIoException = null;
                    break;
                }
                catch (IOException ex) when (attempt < 4)
                {
                    lastIoException = ex;
                    await Task.Delay(150, cancellationToken);
                }
            }
            if (lastIoException is not null) throw lastIoException;

            _cached = null;
            var status = Validate();
            await RecordEventAsync(status, "import", cancellationToken);
            if (!status.IsValid || status.IsDevelopment) throw new InvalidDataException(status.Message);
            return status;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>生成离线授权申请码。申请码不包含密码或私钥，可安全交给授权工具签发。</summary>
    public string CreateActivationRequestCode()
    {
        var request = new LicenseActivationRequest(
            "VisionWorkbench",
            MachineFingerprint(),
            DateTime.UtcNow.ToString("O"),
            Guid.NewGuid().ToString("N"));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions)));
    }

    public bool HasFeature(string feature) => Current.IsValid &&
        Current.Payload?.Features.Contains(feature, StringComparer.OrdinalIgnoreCase) == true;

    public void EnsureFeature(string feature)
    {
        if (!HasFeature(feature)) throw new UnauthorizedAccessException($"当前许可证不包含功能：{feature}");
    }

    public bool EnsureWithinLimit(string name, int value) => Current.IsValid &&
        (Current.Payload?.Limits is null ||
         (Current.Payload.Limits.TryGetValue(name, out var limit) && value <= limit));

    public static string MachineFingerprint()
    {
        var machineGuid = "";
        try { machineGuid = Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "")) ?? ""; } catch { }
        var material = $"{machineGuid}|{Environment.MachineName}|{Environment.ProcessorCount}|{Environment.OSVersion.VersionString}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private bool Verify(LicenseDocument document)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_publicKeyBase64!), out _);
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document.Payload, JsonOptions)),
            Convert.FromBase64String(document.Signature), HashAlgorithmName.SHA256);
    }

    private LicenseStatus Invalid(string message, DateTime now, LicensePayload? payload = null) =>
        new(false, false, "invalid", message, payload, now);

    private async Task RecordEventAsync(LicenseStatus status, string eventType, CancellationToken cancellationToken)
    {
        if (_database is null) return;
        var licenseId = status.Payload?.LicenseId ?? "unknown";
        await using var db = _database.CreateDbContext();
        db.LicenseEvents.Add(new LicenseEventEntity
        {
            LicenseId = licenseId,
            EventType = eventType,
            DetailsJson = JsonSerializer.Serialize(new { status.State, status.IsValid, status.Message }),
            OccurredAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private DateTime? ReadClock()
    {
        var values = new List<DateTime>();
        var evidence = new[]
        {
            ReadProtectedClockFile(_clockStatePath),
            ReadProtectedClockFile(_clockBackupStatePath),
            ReadProtectedClockRegistry(),
        };
        if (evidence.Any(item => item.Present && !item.IsValid))
            throw new InvalidDataException("本机许可证时间状态已损坏或被修改");
        // 注册表副本可能因用户配置迁移、组策略或清理工具丢失；只要两个
        // DPAPI 文件副本仍可解密，就可以安全地用当前时间重新补齐注册表副本。
        // 只有所有文件副本都不可用时，才判定时间状态不可恢复。
        var validFileEvidence = evidence.Take(2).Count(item => item.Present && item.IsValid);
        if (HasClockTopologyMarker() && validFileEvidence == 0)
            throw new InvalidDataException("本机许可证时间状态副本缺失");
        values.AddRange(evidence.Where(item => item.Value is not null).Select(item => item.Value!.Value));

        if (values.Count > 0) return values.Max();

        // 兼容旧版本明文时间记录，并在第一次校验时升级为受保护状态。
        try
        {
            if (File.Exists(_legacyClockStatePath))
            {
                var value = JsonSerializer.Deserialize<DateTime?>(File.ReadAllText(_legacyClockStatePath));
                if (value is not null)
                {
                    SaveClock(value.Value);
                    return value.Value;
                }
            }
        }
        catch { }
        return null;
    }

    private readonly record struct ClockEvidence(bool Present, bool IsValid, DateTime? Value);

    private ClockEvidence ReadProtectedClockFile(string path)
    {
        if (!File.Exists(path)) return new ClockEvidence(false, true, null);
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine));
            var value = JsonSerializer.Deserialize<DateTime?>(json);
            return new ClockEvidence(true, value is not null, value);
        }
        catch
        {
            return new ClockEvidence(true, false, null);
        }
    }

    private ClockEvidence ReadProtectedClockRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClockRegistryPath, writable: false);
            var encoded = key?.GetValue(ClockRegistryValue) as string;
            if (string.IsNullOrWhiteSpace(encoded)) return new ClockEvidence(false, true, null);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(encoded), optionalEntropy: null, DataProtectionScope.LocalMachine));
            var value = JsonSerializer.Deserialize<DateTime?>(json);
            return new ClockEvidence(true, value is not null, value);
        }
        catch
        {
            return new ClockEvidence(true, false, null);
        }
    }

    private void SaveClock(DateTime value)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),
            optionalEntropy: null, DataProtectionScope.LocalMachine);

        var allFilesWritten = true;
        foreach (var path in new[] { _clockStatePath, _clockBackupStatePath })
        {
            try
            {
                var temp = path + ".new-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, protectedBytes);
                File.Move(temp, path, overwrite: true);
            }
            catch { allFilesWritten = false; }
        }

        var registryWritten = false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClockRegistryPath);
            key?.SetValue(ClockRegistryValue, Convert.ToBase64String(protectedBytes), RegistryValueKind.String);
            registryWritten = key is not null;
        }
        catch { }

        if (allFilesWritten && registryWritten)
        {
            try
            {
                File.WriteAllText(_clockTopologyMarkerPath, "2");
                using var key = Registry.CurrentUser.CreateSubKey(ClockRegistryPath);
                key?.SetValue(ClockTopologyRegistryValue, "2", RegistryValueKind.String);
            }
            catch { }
        }
    }

    private bool HasProtectedClockState()
    {
        if (File.Exists(_clockStatePath) || File.Exists(_clockBackupStatePath) || File.Exists(_clockTopologyMarkerPath)) return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClockRegistryPath, writable: false);
            return (key?.GetValue(ClockRegistryValue) is string value && !string.IsNullOrWhiteSpace(value)) ||
                   (key?.GetValue(ClockTopologyRegistryValue) is string topology && !string.IsNullOrWhiteSpace(topology));
        }
        catch { return false; }
    }

    private bool HasClockTopologyMarker()
    {
        if (File.Exists(_clockTopologyMarkerPath)) return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClockRegistryPath, writable: false);
            return key?.GetValue(ClockTopologyRegistryValue) is string value && value == "2";
        }
        catch { return false; }
    }
}
