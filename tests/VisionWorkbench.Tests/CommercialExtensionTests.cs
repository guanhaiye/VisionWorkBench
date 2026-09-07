using System.Text;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class CommercialExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vw-extension-" + Guid.NewGuid().ToString("N"));
    private readonly VisionDbContextFactory _factory;
    public CommercialExtensionTests() { Directory.CreateDirectory(_root); _factory = VisionDbContextFactory.Create(Path.Combine(_root, "visionworkbench.db")); }

    [Fact]
    public async Task Communication_RequestStore_IsIdempotent_AndDetectsConflict()
    {
        var store = new CommunicationRequestStore(_factory);
        var first = await store.BeginAsync("p", "c", "r", "execute", "{\"a\":1}");
        Assert.Equal(CommunicationRequestState.New, first.State);
        await store.CompleteAsync(first.Request!.Id, "{\"ok\":true}");
        var cached = await store.BeginAsync("p", "c", "r", "execute", "{\"a\":1}");
        Assert.Equal(CommunicationRequestState.Cached, cached.State);
        var conflict = await store.BeginAsync("p", "c", "r", "execute", "{\"a\":2}");
        Assert.Equal(CommunicationRequestState.Conflict, conflict.State);
    }

    [Fact]
    public async Task EncryptedBackup_RoundTrips()
    {
        var package = Path.Combine(_root, "backup.vwbackup");
        await new BackupPackageService(_factory).CreateAsync(package);
        var encrypted = Path.Combine(_root, "backup.vwbackup.enc");
        var key = Encoding.UTF8.GetBytes("01234567890123456789012345678901");
        var encryptedService = new EncryptedBackupPackageService(new BackupPackageService(_factory));
        await encryptedService.EncryptAsync(package, encrypted, key);
        await encryptedService.RestoreAsync(encrypted, key);
        Assert.True(File.Exists(encrypted));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
