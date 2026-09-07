using System.Text.Json;

namespace VisionWorkbench.Application;

public sealed record UpdatePreparation(string StagingDirectory, string Version, IReadOnlyList<string> Files);

/// <summary>Stages an offline update and keeps a recoverable rollback marker. Activation is deliberately external to the running WPF process.</summary>
public sealed class OfflineUpdateService(ReleaseManifestService manifests)
{
    public async Task<UpdatePreparation> PrepareAsync(
        string packageDirectory, string stagingRoot, string? publicKeyBase64 = null, CancellationToken ct = default)
    {
        var manifest = await manifests.ReadAndVerifyAsync(packageDirectory,
            requireSignature: !string.IsNullOrWhiteSpace(publicKeyBase64), publicKeyBase64: publicKeyBase64, ct: ct);
        var sourceRoot = Path.GetFullPath(packageDirectory);
        var destination = Path.Combine(Path.GetFullPath(stagingRoot), manifest.Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        foreach (var item in manifest.Files)
        {
            var source = Path.Combine(sourceRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(destination, item.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
        File.Copy(Path.Combine(sourceRoot, "release-manifest.json"), Path.Combine(destination, "release-manifest.json"));
        await File.WriteAllTextAsync(Path.Combine(destination, "update-ready.json"), JsonSerializer.Serialize(new
        {
            manifest.Product, manifest.Version, manifest.Build, preparedAtUtc = DateTime.UtcNow,
        }), ct);
        return new UpdatePreparation(destination, manifest.Version, manifest.Files.Select(x => x.Path).ToArray());
    }

    public static void WriteRollbackMarker(string currentDirectory, string previousDirectory)
    {
        var current = Path.GetFullPath(currentDirectory);
        var previous = Path.GetFullPath(previousDirectory);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "rollback-marker.json"), JsonSerializer.Serialize(new
        {
            previousDirectory = previous, createdAtUtc = DateTime.UtcNow,
        }));
    }

    public static string? ReadRollbackDirectory(string currentDirectory)
    {
        var marker = Path.Combine(Path.GetFullPath(currentDirectory), "rollback-marker.json");
        if (!File.Exists(marker)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var path = document.RootElement.GetProperty("previousDirectory").GetString();
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (JsonException) { return null; }
    }
}
