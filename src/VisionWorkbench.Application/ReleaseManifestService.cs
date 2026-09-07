using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionWorkbench.Application;

public sealed record ReleaseFile(string Path, string Sha256, long Size);

public sealed record ReleaseManifest(
    string Product,
    string Version,
    string Build,
    string Runtime,
    string DatabaseTargetVersion,
    IReadOnlyList<ReleaseFile> Files,
    string? Signature = null,
    string? SignatureAlgorithm = null);

/// <summary>Verifies release contents and an optional detached ECDSA P-256 manifest signature.</summary>
public sealed class ReleaseManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public async Task<ReleaseManifest> ReadAndVerifyAsync(
        string packageDirectory,
        bool requireSignature = false,
        string? publicKeyBase64 = null,
        CancellationToken ct = default)
    {
        var root = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(root, "release-manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Release manifest not found", manifestPath);
        await using var input = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ReleaseManifest>(input, JsonOptions, ct)
            ?? throw new InvalidDataException("Release manifest is invalid");
        if (!string.Equals(manifest.Product, "VisionWorkbench", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release product does not match");
        if (manifest.Files.Count == 0) throw new InvalidDataException("Release manifest has no files");

        foreach (var item in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || item.Path.Contains('\\') || Path.IsPathRooted(item.Path)
                || item.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException($"Unsafe release path: {item.Path}");
            var full = Path.GetFullPath(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(full, root) || !File.Exists(full))
                throw new InvalidDataException($"Release file is missing or unsafe: {item.Path}");
            var info = new FileInfo(full);
            if (info.Length != item.Size)
                throw new InvalidDataException($"Release file size mismatch: {item.Path}");
            await using var fileStream = File.OpenRead(full);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fileStream, ct));
            if (!string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Release file hash mismatch: {item.Path}");
        }

        if (requireSignature && string.IsNullOrWhiteSpace(manifest.Signature))
            throw new CryptographicException("Release manifest is not signed");
        if (!string.IsNullOrWhiteSpace(manifest.Signature))
        {
            if (!string.Equals(manifest.SignatureAlgorithm, "ECDSA_P256_SHA256", StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Unsupported release signature algorithm");
            if (string.IsNullOrWhiteSpace(publicKeyBase64))
                throw new CryptographicException("Release public key is not configured");
            if (!VerifySignature(manifest, publicKeyBase64))
                throw new CryptographicException("Release manifest signature verification failed");
        }
        return manifest;
    }

    public static bool VerifySignature(ReleaseManifest manifest, string publicKeyBase64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(CanonicalUnsignedJson(manifest)),
            Convert.FromBase64String(manifest.Signature ?? ""), HashAlgorithmName.SHA256);
    }

    public static string CanonicalUnsignedJson(ReleaseManifest manifest) =>
        JsonSerializer.Serialize(manifest with { Signature = null, SignatureAlgorithm = null }, JsonOptions);

    public static string Sign(ReleaseManifest manifest, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return Convert.ToBase64String(privateKey.SignData(
            Encoding.UTF8.GetBytes(CanonicalUnsignedJson(manifest)), HashAlgorithmName.SHA256));
    }

    public static bool CanUpdate(bool productionRunning, bool hasPendingTasks) => !productionRunning && !hasPendingTasks;

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
