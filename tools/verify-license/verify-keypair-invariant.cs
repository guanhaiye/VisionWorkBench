// 验证签发工具配对闸门的核心不变量：
// 1) 官方私钥导出的公钥 == 两端内置的官方公钥常量（闸门放行正常签发）
// 2) 随机新密钥 != 官方公钥常量（闸门拦截错误密钥）
using System.Security.Cryptography;

AppContext.SetData("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

const string officialPublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEks147MY/s3RH1OVtbQeaY0eKemW2N+gOXpMcqctBpzFOdDd8nvIm7SI7f/FNNtPiu4W7sAaeWw84TLTPPXW3mA==";

using (var official = ECDsa.Create())
{
    official.ImportFromPem(File.ReadAllText(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionWorkbenchLicenseIssuer", "license-signing-private.pem")));
    var derived = Convert.ToBase64String(official.ExportSubjectPublicKeyInfo());
    Console.WriteLine($"官方私钥派生公钥 == 常量: {derived == officialPublicKey}");
}

using (var random = ECDsa.Create(ECCurve.NamedCurves.nistP256))
{
    var derived = Convert.ToBase64String(random.ExportSubjectPublicKeyInfo());
    Console.WriteLine($"随机密钥派生公钥 == 常量: {derived == officialPublicKey} (应为 False)");
}
