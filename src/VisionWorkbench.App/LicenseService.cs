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
    string RequestId,
    string? PublicKeyId = null);

/// <summary>客户端只持有公钥，离线校验签名和设备绑定；正式运行必须提供有效许可证。</summary>
public sealed class LicenseService : IDisposable
{
    public const string OfficialPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEoieaqt2/vVECWdyLfmHc+tJOTe1WKtGtQ8fWN6u0QD9+Jz8/CkbzFF7aHwZua5MzPL6HJvyBzi+RwDzxshSVhA==";
    private const long MaximumLicenseBytes = 1024 * 1024;
    private bool _disposed;
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
    private volatile LicenseStatus? _cached;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    private const string ClockRegistryPath = @"Software\VisionWorkbench\License";
    private const string ClockRegistryValue = "ProtectedClock";
    private const string ClockTopologyRegistryValue = "ProtectedClockTopology";

    public LicenseService(string dataDirectory, string? publicKeyBase64 = null, VisionDbContextFactory? database = null)
    {
        dataDirectory = Path.GetFullPath(dataDirectory);
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
        _publicKeyBase64 = publicKeyBase64 ?? OfficialPublicKey;
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
            var cached = _cached;
            if (cached is null || now < cached.CheckedAtUtc ||
                now - cached.CheckedAtUtc >= TimeSpan.FromMinutes(1) ||
                (cached.IsValid && now >= cached.Payload!.ExpiresAtUtc))
                return Validate();
            return cached;
        }
    }

    public string PublicKeyId => Convert.ToHexString(
        SHA256.HashData(Convert.FromBase64String(_publicKeyBase64!)))[..16];

    public LicenseStatus Validate()
    {
        lock (_validationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _cached = ValidateFile(_licensePath, saveClock: true);
        }
    }

    // Candidate validation has no side effects: an invalid import must never destroy
    // the installed license or advance/reset the protected anti-rollback clock.
    private LicenseStatus ValidateFile(string path, bool saveClock)
    {
        var now = DateTime.UtcNow;
        var monotonicNow = _processStartUtc + Stopwatch.GetElapsedTime(_processStartTimestamp);
        if (now < monotonicNow.AddMinutes(-5))
            return Invalid("检测到运行期间系统时间回拨，许可证暂时受限", now);
        try
        {
            if (!File.Exists(path))
                return new LicenseStatus(false, false, "missing", "缺少许可证，请导入有效许可证", null, now);
            if (new FileInfo(path).Length > MaximumLicenseBytes)
                return Invalid("许可证文件超过 1 MB 大小限制", now);
            var document = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("许可证为空");
            var payload = document.Payload;
            if (payload is null || string.IsNullOrWhiteSpace(document.Signature) ||
                string.IsNullOrWhiteSpace(payload.LicenseId) ||
                string.IsNullOrWhiteSpace(payload.MachineFingerprint) ||
                payload.Features is null || payload.Features.Any(string.IsNullOrWhiteSpace) ||
                payload.Limits?.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0) == true)
                throw new InvalidDataException("许可证缺少必要字段或包含无效功能/数量限制");
            if (payload.NotBeforeUtc.Kind != DateTimeKind.Utc || payload.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
                payload.ExpiresAtUtc <= payload.NotBeforeUtc)
                throw new InvalidDataException("许可证有效期格式无效，须为 UTC 且到期时间晚于生效时间");
            if (!string.Equals(document.SignatureAlgorithm, "ECDSA_P256_SHA256", StringComparison.Ordinal))
                throw new InvalidDataException("不支持的许可证签名算法");
            if (string.IsNullOrWhiteSpace(_publicKeyBase64))
                return Invalid("客户端未配置许可证公钥", now);
            if (!Verify(document))
                return Invalid($"许可证签名校验失败（客户端公钥标识：{PublicKeyId}）。请确认签发工具使用配对私钥，且许可证内容未经修改。", now);
            if (!string.Equals(payload.Product, "VisionWorkbench", StringComparison.OrdinalIgnoreCase))
                return Invalid("许可证产品不匹配", now);
            if (!string.Equals(payload.MachineFingerprint, MachineFingerprint(), StringComparison.OrdinalIgnoreCase))
                return Invalid("许可证未绑定当前设备", now);
            var last = ReadClock();
            if (last is null && HasProtectedClockState())
                return Invalid("本机许可证时间状态无效，请联系授权方恢复时间状态", now);
            if (last is not null && now < last.Value.AddMinutes(-5))
                return Invalid("检测到系统时间回拨，许可证暂时受限", now);
            if (now < payload.NotBeforeUtc) return Invalid("许可证尚未生效", now, payload);
            if (now >= payload.ExpiresAtUtc) return Invalid("许可证已过期", now, payload);
            if (saveClock) SaveClock(now);
            return new LicenseStatus(true, false, "valid", $"许可证有效期至 {payload.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", payload, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   ArgumentException or FormatException or CryptographicException or InvalidDataException)
        {
            return Invalid($"许可证无效：{ex.Message}", now);
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
        await _importGate.WaitAsync(cancellationToken);
        var temp = _licensePath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(source)) throw new FileNotFoundException("许可证文件不存在", source);
            if (new FileInfo(source).Length > MaximumLicenseBytes)
                throw new InvalidDataException("许可证文件超过 1 MB 大小限制");
            // Open with read sharing only so an issuer cannot change the file during copying.
            await using (var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = File.Create(temp))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            LicenseStatus status;
            lock (_validationGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                status = ValidateFile(temp, saveClock: false);
                if (!status.IsValid) throw new InvalidDataException(status.Message);
                // Validate the exact bytes that will be installed before replacing anything.
                File.Move(temp, _licensePath, overwrite: true);
                _cached = status;
                SaveClock(status.CheckedAtUtc);
            }
            // Commit has completed. Cancellation/audit failure must not report a failed
            // activation while leaving a newly installed valid license on disk.
            try { await RecordEventAsync(status, "import", CancellationToken.None); }
            catch (Exception ex) { Trace.TraceError($"许可证已激活，但记录导入事件失败：{ex}"); }
            return status;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _importGate.Release();
        }
    }

    /// <summary>生成离线授权申请码。申请码不包含密码或私钥，可安全交给授权工具签发。</summary>
    public string CreateActivationRequestCode()
    {
        var request = new LicenseActivationRequest(
            "VisionWorkbench",
            MachineFingerprint(),
            DateTime.UtcNow.ToString("O"),
            Guid.NewGuid().ToString("N"),
            PublicKeyId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions)));
    }

    public bool HasFeature(string feature)
    {
        var status = Current;
        return status.IsValid && status.Payload?.Features.Contains(feature, StringComparer.OrdinalIgnoreCase) == true;
    }

    public void EnsureFeature(string feature)
    {
        if (!HasFeature(feature)) throw new UnauthorizedAccessException($"当前许可证不包含功能：{feature}");
    }

    public bool EnsureWithinLimit(string name, int value)
    {
        var status = Current;
        if (!status.IsValid || value < 0) return false;
        var entry = status.Payload?.Limits?.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return entry is null || entry.Value.Key is null || value <= entry.Value.Value;
    }

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
        var publicKey = Convert.FromBase64String(_publicKeyBase64!);
        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
        if (consumed != publicKey.Length || ecdsa.KeySize != 256) return false;
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document.Payload, JsonOptions)),
            Convert.FromBase64String(document.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
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
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),
                optionalEntropy: null, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            Trace.TraceError($"许可证时间状态保存失败：{ex.Message}");
            return;
        }

        var allFilesWritten = true;
        foreach (var path in new[] { _clockStatePath, _clockBackupStatePath })
        {
            var temp = path + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, protectedBytes);
                File.Move(temp, path, overwrite: true);
            }
            catch { allFilesWritten = false; }
            finally { try { File.Delete(temp); } catch { } }
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
    public void Dispose()
    {
        lock (_validationGate)
        {
            if (_disposed) return;
            _disposed = true;
            _backgroundValidationTimer.Dispose();
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
        }
    }

}
