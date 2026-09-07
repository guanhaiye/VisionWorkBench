using System.Security.Cryptography;
using System.Text.Json;
using VisionWorkbench.Application;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class CommercialGovernanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vw-governance-" + Guid.NewGuid().ToString("N"));
    private readonly VisionDbContextFactory _factory;

    public CommercialGovernanceTests()
    {
        Directory.CreateDirectory(_root);
        _factory = VisionDbContextFactory.Create(Path.Combine(_root, "visionworkbench.db"));
    }

    [Fact]
    public async Task ReleaseManifest_Verifies_Signature_And_File_Size()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Path.Combine(_root, "release");
        Directory.CreateDirectory(package);
        var payloadPath = Path.Combine(package, "VisionWorkbench.dll");
        await File.WriteAllTextAsync(payloadPath, "signed release");
        var bytes = await File.ReadAllBytesAsync(payloadPath);
        var manifest = new ReleaseManifest("VisionWorkbench", "1.0.0", "test", "win-x64",
            DatabaseMigrationService.CurrentVersion,
            [new ReleaseFile("VisionWorkbench.dll", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length)]);
        var signed = manifest with
        {
            SignatureAlgorithm = "ECDSA_P256_SHA256",
            Signature = ReleaseManifestService.Sign(manifest, key),
        };
        await File.WriteAllTextAsync(Path.Combine(package, "release-manifest.json"),
            JsonSerializer.Serialize(signed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var service = new ReleaseManifestService();
        var verified = await service.ReadAndVerifyAsync(package, true, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.Equal("1.0.0", verified.Version);
        await File.AppendAllTextAsync(payloadPath, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAndVerifyAsync(package, true, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Session_Locks_After_Inactivity_And_Ends_Cleanly()
    {
        var session = new SessionService(TimeSpan.FromMilliseconds(1));
        session.Begin("operator", "operator");
        Thread.Sleep(20);
        Assert.True(session.Current.IsLocked);
        Assert.False(session.Touch());
        session.End();
        Assert.False(session.Current.IsAuthenticated);
    }

    [Fact]
    public async Task OfflineUpdate_Stages_Manifest_And_Reads_Rollback_Marker()
    {
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(package);
        var file = Path.Combine(package, "VisionWorkbench.exe");
        await File.WriteAllTextAsync(file, "payload");
        var content = await File.ReadAllBytesAsync(file);
        var manifest = new ReleaseManifest("VisionWorkbench", "1.0.1", "test", "win-x64", DatabaseMigrationService.CurrentVersion,
            [new ReleaseFile("VisionWorkbench.exe", Convert.ToHexString(SHA256.HashData(content)), content.Length)]);
        await File.WriteAllTextAsync(Path.Combine(package, "release-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var staged = await new OfflineUpdateService(new ReleaseManifestService()).PrepareAsync(package, Path.Combine(_root, "staging"));
        Assert.True(File.Exists(Path.Combine(staged.StagingDirectory, "VisionWorkbench.exe")));
        var current = Path.Combine(_root, "current");
        OfflineUpdateService.WriteRollbackMarker(current, _root);
        Assert.Equal(Path.GetFullPath(_root), OfflineUpdateService.ReadRollbackDirectory(current));
    }

    [Fact]
    public async Task Retention_Uses_Status_Specific_Cutoffs_And_Protects_Evidence()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Tasks.AddRange(
                new TaskEntity { Id = 1, Name = "retention-1", StationCode = "ST-001", CameraProviderId = "test", CameraDeviceId = "test", PluginId = "test" },
                new TaskEntity { Id = 2, Name = "retention-2", StationCode = "ST-002", CameraProviderId = "test", CameraDeviceId = "test", PluginId = "test" },
                new TaskEntity { Id = 3, Name = "retention-3", StationCode = "ST-003", CameraProviderId = "test", CameraDeviceId = "test", PluginId = "test" });
            await db.SaveChangesAsync();
            db.InspectionRecords.AddRange(
                new InspectionRecordEntity { TaskId = 1, StationCode = "ST-001", ProjectId = "default", Status = "ok", StartedAt = DateTime.UtcNow.AddDays(-10) },
                new InspectionRecordEntity { TaskId = 2, StationCode = "ST-002", ProjectId = "default", Status = "ng", StartedAt = DateTime.UtcNow.AddDays(-10) },
                new InspectionRecordEntity { TaskId = 3, StationCode = "ST-003", ProjectId = "default", Status = "ng", StartedAt = DateTime.UtcNow.AddDays(-10), AnnotatedImagePath = "evidence.png" });
            await db.SaveChangesAsync();
        }
        var retention = new DataRetentionService(_factory);
        var preview = await retention.PreviewAsync(new RetentionPolicy(OkDays: 5, NgDays: 30));
        Assert.Equal(1, preview.CandidateRecords);
        Assert.Equal(1, preview.ProtectedRecords);
        Assert.Equal(1, await retention.DeleteBatchAsync(new RetentionPolicy(OkDays: 5, NgDays: 30)));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
