using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record UpdatePreparation(string StagingDirectory, string Version, IReadOnlyList<string> Files);

/// <summary>Stages a verified offline update; activation is external to the running WPF process.</summary>
public sealed class OfflineUpdateService(ReleaseManifestService manifests)
{
    public async Task<UpdatePreparation> PrepareAsync(
        string packageDirectory, string stagingRoot, string? publicKeyBase64 = null, CancellationToken ct = default)
    {
        var requireSignature = !string.IsNullOrWhiteSpace(publicKeyBase64);
        var manifest = await manifests.ReadAndVerifyAsync(packageDirectory, requireSignature, publicKeyBase64, ct);
        var sourceRoot = Path.GetFullPath(packageDirectory);
        var destination = ReleaseManifestService.SafeFilePath(Path.GetFullPath(stagingRoot), manifest.Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var item in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var source = ReleaseManifestService.SafeFilePath(sourceRoot, item.Path);
                var target = ReleaseManifestService.SafeFilePath(destination, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct);
            }
            // Persist the already verified manifest, then verify copied bytes as well:
            // source files can change between the first verification and staging.
            await File.WriteAllTextAsync(Path.Combine(destination, "release-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
            await manifests.ReadAndVerifyAsync(destination, requireSignature, publicKeyBase64, ct);
            await File.WriteAllTextAsync(Path.Combine(destination, "update-ready.json"), JsonSerializer.Serialize(new
            {
                manifest.Product, manifest.Version, manifest.Build, preparedAtUtc = DateTime.UtcNow,
            }), ct);
            return new UpdatePreparation(destination, manifest.Version, manifest.Files.Select(x => x.Path).ToArray());
        }
        catch
        {
            try { Directory.Delete(destination, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void WriteRollbackMarker(string currentDirectory, string previousDirectory)
    {
        var current = Path.GetFullPath(currentDirectory);
        var previous = Path.GetFullPath(previousDirectory);
        ReleaseManifestService.EnsureNoLinks(current);
        Directory.CreateDirectory(current);
        var temporary = Path.Combine(current, "rollback-marker-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { previousDirectory = previous, createdAtUtc = DateTime.UtcNow }));
            File.Move(temporary, Path.Combine(current, "rollback-marker.json"), overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    public static string? ReadRollbackDirectory(string currentDirectory)
    {
        var marker = Path.Combine(Path.GetFullPath(currentDirectory), "rollback-marker.json");
        if (!File.Exists(marker)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("previousDirectory", out var value)
                || value.ValueKind != JsonValueKind.String) return null;
            var path = value.GetString();
            return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? null : Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}