using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionWorkbench.App;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class CommercialFoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vw-commercial-" + Guid.NewGuid().ToString("N"));
    private readonly VisionDbContextFactory _factory;

    public CommercialFoundationTests()
    {
        Directory.CreateDirectory(_root);
        _factory = VisionDbContextFactory.Create(Path.Combine(_root, "visionworkbench.db"));
    }

    [Fact]
    public async Task Schema_Seeds_Rbac_And_Audit_Chain_Detects_Tampering()
    {
        var access = new AccessControlService(_factory);
        await access.EnsureSeededAsync();
        await using (var db = _factory.CreateDbContext())
        {
            var role = await db.Roles.SingleAsync(item => item.Code == "engineer");
            var user = new UserEntity { UserName = "engineer", PasswordHash = "pbkdf2$test" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.UserRoles.Add(new UserRoleEntity { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        Assert.True(await access.HasPermissionAsync("engineer", "recipe.publish"));
        Assert.False(await access.HasPermissionAsync("engineer", "user.manage"));

        var audit = new AuditService(_factory);
        await audit.RecordAsync("recipe.publish", "recipe", "r-1", "engineer");
        await audit.RecordAsync("recipe.rollback", "recipe", "r-1", "engineer");
        Assert.True(await audit.VerifyChainAsync());
        await using (var db = _factory.CreateDbContext())
        {
            var first = await db.AuditEvents.OrderBy(item => item.Id).FirstAsync();
            first.DetailsJson = "{\"changed\":true}";
            await db.SaveChangesAsync();
        }
        Assert.False(await audit.VerifyChainAsync());
    }

    [Fact]
    public async Task Full_Backup_Package_Roundtrips_And_Rejects_Tampered_Entry()
    {
        await new TaskRepository(_factory).SaveAsync(new TaskEntity
        {
            Name = "商用备份测试", CameraProviderId = "image-folder", CameraDeviceId = "images", PluginId = "p"
        });
        var package = Path.Combine(_root, "test.vwbackup");
        var result = await new BackupPackageService(_factory).CreateAsync(package);
        Assert.True(File.Exists(package));
        Assert.Contains("database/visionworkbench.db", result.Entries);
        Assert.Equal(result.Sha256, BackupPackageService.ComputeSha256(package));

        await new TaskRepository(_factory).SaveAsync(new TaskEntity
        {
            Name = "备份后数据", CameraProviderId = "image-folder", CameraDeviceId = "images", PluginId = "p"
        });
        await new BackupPackageService(_factory).RestoreAsync(package);
        Assert.Single(await new TaskRepository(_factory).ListAsync());

        var tampered = Path.Combine(_root, "tampered.vwbackup");
        File.Copy(package, tampered);
        using (var archive = System.IO.Compression.ZipFile.Open(tampered, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("database/visionworkbench.db")!;
            entry.Delete();
            var replacement = archive.CreateEntry("database/visionworkbench.db");
            using var writer = new StreamWriter(replacement.Open());
            writer.Write("not a database");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupPackageService(_factory).RestoreAsync(tampered));
    }

    [Fact]
    public async Task License_Import_Verifies_Signature_And_Device_Binding()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var payload = new LicensePayload(
            "LIC-TEST-001", "测试客户", "VisionWorkbench", "standard",
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(1), LicenseService.MachineFingerprint(),
            ["production.run", "model.manage"]);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, options));
        var document = new LicenseDocument(payload,
            Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256)));
        var source = Path.Combine(_root, "license.json");
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(document, options));
        var service = new LicenseService(_root, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), _factory);

        var valid = await service.ImportAsync(source);
        Assert.True(valid.IsValid);
        Assert.True(service.HasFeature("production.run"));
        Assert.False(string.IsNullOrWhiteSpace(service.CreateActivationRequestCode()));
        await using (var db = _factory.CreateDbContext())
            Assert.Contains(await db.LicenseEvents.ToListAsync(), item => item.EventType == "import");

        await File.WriteAllTextAsync(service.LicensePath,
            JsonSerializer.Serialize(document with { Payload = payload with { Customer = "被篡改" } }, options));
        Assert.False(service.Validate().IsValid);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
