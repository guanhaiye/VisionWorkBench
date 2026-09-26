using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// 与 VisionWorkbench.App LicenseService.Verify / 签发工具完全一致的序列化选项
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
};

var doc = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(args[0]), options)
          ?? throw new InvalidDataException("license is null");
var payloadJson = JsonSerializer.Serialize(doc.Payload, options);
Console.WriteLine($"重序列化载荷: {payloadJson}");
Console.WriteLine($"签名算法: {doc.SignatureAlgorithm}");
var signature = Convert.FromBase64String(doc.Signature);


// 设备指纹（与客户端 MachineFingerprint 完全一致）
var machineGuid = Convert.ToString(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "")) ?? "";
var material = $"{machineGuid}|{Environment.MachineName}|{Environment.ProcessorCount}|{Environment.OSVersion.VersionString}";
var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)));
Console.WriteLine($"本机设备码: {fingerprint}");
Console.WriteLine($"许可证设备码匹配: {string.Equals(fingerprint, doc.Payload.MachineFingerprint, StringComparison.OrdinalIgnoreCase)}");
var nowUtc = DateTime.UtcNow;
Console.WriteLine($"时间窗有效: {nowUtc >= doc.Payload.NotBeforeUtc && nowUtc <= doc.Payload.ExpiresAtUtc} (now={nowUtc:O}, notBefore={doc.Payload.NotBeforeUtc:O}, expires={doc.Payload.ExpiresAtUtc:O})");

foreach (var keyFile in args.Skip(1))
{
    var keyBase64 = File.ReadAllText(keyFile).Trim();
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keyBase64), out _);
    var ok = ecdsa.VerifyData(Encoding.UTF8.GetBytes(payloadJson), signature, HashAlgorithmName.SHA256);
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {keyFile}");
}

public sealed record LicensePayload(
    string LicenseId, string Customer, string Product, string Edition,
    DateTime NotBeforeUtc, DateTime ExpiresAtUtc, string MachineFingerprint,
    IReadOnlyList<string> Features, IReadOnlyDictionary<string, int>? Limits = null);

public sealed record LicenseDocument(LicensePayload Payload, string Signature, string SignatureAlgorithm = "ECDSA_P256_SHA256");
