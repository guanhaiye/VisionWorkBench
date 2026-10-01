using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionWorkbench.App;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class LicenseRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vw-license-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private LicenseService CreateService() => new(Path.Combine(_root, "installed"), Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
    private LicensePayload Payload() => new("LIC-REGRESSION", "客户中文", "VisionWorkbench", "Professional",
        DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(1), LicenseService.MachineFingerprint(), ["detection"]);
    private async Task<string> WriteLicense(LicensePayload payload, bool tamper = false)
    {
        Directory.CreateDirectory(_root);
        var signature = Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _options)),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var document = new LicenseDocument(tamper ? payload with { Customer = "tampered" } : payload, signature);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".vwlicense");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, new JsonSerializerOptions(_options) { WriteIndented = true }));
        return path;
    }

    [Fact]
    public void UserSettings_Cannot_Override_Or_Persist_The_Trust_Root()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"LicensePublicKey\":\"stale-or-attacker-key\"}")!;
        Assert.Equal(LicenseService.OfficialPublicKey, settings.LicensePublicKey);
        Assert.DoesNotContain("LicensePublicKey", JsonSerializer.Serialize(settings));
    }

    [Fact]
    public async Task Import_Validates_Exact_Issuer_Format_And_Survives_Restart()
    {
        var source = await WriteLicense(Payload());
        using (var service = CreateService())
        {
            Assert.True((await service.ImportAsync(source)).IsValid);
            Assert.True(service.HasFeature("detection"));
            Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(service.LicensePath));
            Assert.True((await service.ImportAsync(service.LicensePath)).IsValid);
        }
        using var reopened = CreateService();
        Assert.True(reopened.Validate().IsValid);
    }

    [Fact]
    public async Task Module_Selection_Restricts_Data_And_Model_Access()
    {
        using var service = CreateService();
        var payload = Payload() with
        {
            Features = ["data-model.detection"],
            Modules = ["data-model.detection"],
        };
        await service.ImportAsync(await WriteLicense(payload));

        Assert.True(service.HasModule("data-model.detection"));
        Assert.False(service.HasModule("data-model.semantic-segmentation"));
        Assert.False(service.HasModule("data-model.pose"));
    }

    [Fact]
    public async Task Invalid_Import_Preserves_Installed_Bytes_And_Cached_Activation()
    {
        using var service = CreateService();
        await service.ImportAsync(await WriteLicense(Payload()));
        var original = await File.ReadAllBytesAsync(service.LicensePath);
        var invalid = await WriteLicense(Payload(), tamper: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(invalid));
        Assert.Equal(original, await File.ReadAllBytesAsync(service.LicensePath));
        Assert.True(service.Current.IsValid);
        Assert.True(service.Validate().IsValid);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(service.LicensePath)!, "*.new-*"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"payload\":null,\"signature\":\"abc\"}")]
    [InlineData("{\"payload\":{},\"signature\":null}")]
    public async Task Malformed_License_Is_Rejected_Without_Crashing_Or_Installing(string json)
    {
        using var service = CreateService();
        var path = Path.Combine(_root, "malformed.vwlicense");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(path));
        Assert.False(File.Exists(service.LicensePath));
        Assert.Equal("missing", service.Current.State);
    }

    [Fact]
    public async Task Wrong_Machine_Expired_And_Future_Licenses_Do_Not_Replace_Valid_License()
    {
        using var service = CreateService();
        await service.ImportAsync(await WriteLicense(Payload()));
        var before = await File.ReadAllTextAsync(service.LicensePath);
        foreach (var payload in new[] {
            Payload() with { MachineFingerprint = new string('0', 64) },
            Payload() with { NotBeforeUtc = DateTime.UtcNow.AddDays(-2), ExpiresAtUtc = DateTime.UtcNow.AddDays(-1) },
            Payload() with { NotBeforeUtc = DateTime.UtcNow.AddDays(1), ExpiresAtUtc = DateTime.UtcNow.AddDays(2) },
            Payload() with { Features = null! },
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(WriteLicense(payload).GetAwaiter().GetResult()));
        Assert.Equal(before, await File.ReadAllTextAsync(service.LicensePath));
    }

    [Fact]
    public async Task Cancelled_Import_Does_Not_Change_Activation()
    {
        using var service = CreateService();
        await service.ImportAsync(await WriteLicense(Payload()));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(service.LicensePath, cancel.Token));
        Assert.True(service.Validate().IsValid);
    }

    [Fact]
    public async Task Concurrent_Imports_Leave_A_Complete_Valid_Document()
    {
        using var service = CreateService();
        var source = await WriteLicense(Payload());
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.ImportAsync(source)));
        Assert.All(results, status => Assert.True(status.IsValid));
        Assert.True(service.Validate().IsValid);
    }

    [Fact]
    public async Task Oversized_License_Is_Rejected_Before_Installation()
    {
        using var service = CreateService();
        var path = Path.Combine(_root, "large.vwlicense");
        await File.WriteAllTextAsync(path, new string(' ', 1024 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(path));
        Assert.False(File.Exists(service.LicensePath));
    }

    [Fact]
    public void Request_Identifies_The_Client_Trust_Root()
    {
        using var service = CreateService();
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(service.CreateActivationRequestCode()));
        var request = JsonSerializer.Deserialize<LicenseActivationRequest>(json, _options)!;
        Assert.Equal(service.PublicKeyId, request.PublicKeyId);
        Assert.Equal(LicenseService.MachineFingerprint(), request.MachineFingerprint);
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
