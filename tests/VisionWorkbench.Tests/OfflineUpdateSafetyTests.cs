using System.Security.Cryptography;
using System.Text.Json;
using VisionWorkbench.Application;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class OfflineUpdateSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vw-update-safety-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OfflineUpdateSafetyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Signed_Release_Is_Verified_Again_After_Staging()
    {
        var package = await CreatePackageAsync("1.2.3");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(Path.Combine(package, "release-manifest.json")), JsonOptions)!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        manifest = manifest with { Signature = ReleaseManifestService.Sign(manifest, key), SignatureAlgorithm = "ECDSA_P256_SHA256" };
        await WriteManifestAsync(package, manifest);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var preparation = await new OfflineUpdateService(new ReleaseManifestService()).PrepareAsync(package, Path.Combine(_root, "staging"), publicKey);
        Assert.True(File.Exists(Path.Combine(preparation.StagingDirectory, "update-ready.json")));
        Assert.Equal("1.2.3", (await new ReleaseManifestService().ReadAndVerifyAsync(preparation.StagingDirectory, true, publicKey)).Version);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/escape")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Unsafe_Version_Cannot_Escape_Staging(string version)
    {
        var package = await CreatePackageAsync(version);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new OfflineUpdateService(new ReleaseManifestService()).PrepareAsync(package, Path.Combine(_root, "staging")));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("file.dll:stream")]
    [InlineData("folder./file.dll")]
    [InlineData("folder /file.dll")]
    [InlineData("CON")]
    [InlineData("release-manifest.json")]
    [InlineData("update-ready.json")]
    public async Task Unsafe_Or_Reserved_Payload_Paths_Are_Rejected(string path)
    {
        var package = await CreatePackageAsync("1.0");
        await WriteManifestAsync(package, new ReleaseManifest("VisionWorkbench", "1.0", "test", "win-x64", "test", [new ReleaseFile(path, new string('A',64), 0)]));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new ReleaseManifestService().ReadAndVerifyAsync(package));
    }

    [Fact]
    public async Task Missing_And_Duplicate_File_Lists_Are_Rejected()
    {
        var package = await CreatePackageAsync("1.0");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(Path.Combine(package, "release-manifest.json")), JsonOptions)!;
        await WriteManifestAsync(package, manifest with { Files = null! });
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new ReleaseManifestService().ReadAndVerifyAsync(package));
        await WriteManifestAsync(package, manifest with { Files = [manifest.Files[0], manifest.Files[0] with { Path = "PROGRAM.DLL" }] });
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new ReleaseManifestService().ReadAndVerifyAsync(package));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"previousDirectory\":12}")]
    [InlineData("{\"previousDirectory\":\"../outside\"}")]
    [InlineData("invalid JSON")]
    public void Malformed_Rollback_Marker_Is_Ignored(string json)
    {
        File.WriteAllText(Path.Combine(_root, "rollback-marker.json"), json);
        Assert.True(OfflineUpdateService.ReadRollbackDirectory(_root) is null);
    }

    private async Task<string> CreatePackageAsync(string version)
    {
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(package);
        var payload = Path.Combine(package, "program.dll");
        await File.WriteAllTextAsync(payload, "verified payload");
        var bytes = await File.ReadAllBytesAsync(payload);
        await WriteManifestAsync(package, new ReleaseManifest("VisionWorkbench", version, "test", "win-x64", "test",
            [new ReleaseFile("program.dll", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length)]));
        return package;
    }

    private static Task WriteManifestAsync(string package, ReleaseManifest manifest) =>
        File.WriteAllTextAsync(Path.Combine(package, "release-manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}