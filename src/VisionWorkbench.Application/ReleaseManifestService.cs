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
        var manifestPath = SafeFilePath(root, "release-manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Release manifest not found", manifestPath);
        await using var input = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ReleaseManifest>(input, JsonOptions, ct)
            ?? throw new InvalidDataException("Release manifest is invalid");
        if (!string.Equals(manifest.Product, "VisionWorkbench", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release product does not match");
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Version, @"\A[0-9A-Za-z][0-9A-Za-z._+\-]{0,127}\z"))
            throw new InvalidDataException("Release version is unsafe or missing");
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("Release manifest has no files");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in manifest.Files)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Path) || !names.Add(item.Path)
                || item.Path.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase)
                || item.Path.Equals("update-ready.json", StringComparison.OrdinalIgnoreCase)
                || item.Path.Equals("rollback-marker.json", StringComparison.OrdinalIgnoreCase)
                || item.Size < 0 || item.Sha256 is null
                || !System.Text.RegularExpressions.Regex.IsMatch(item.Sha256, @"\A[0-9A-Fa-f]{64}\z"))
                throw new InvalidDataException("Release entry is invalid, duplicated or reserved");
            var full = SafeFilePath(root, item.Path);
            if (!File.Exists(full))
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

    internal static string SafeFilePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new InvalidDataException($"Unsafe release path: {relativePath}");
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.IndexOfAny("<>:\"|?*".ToCharArray()) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(segment, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException($"Unsafe release path: {relativePath}");
        }
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Release path escapes package: {relativePath}");
        EnsureNoLinks(full);
        return full;
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update paths cannot contain filesystem links");
        }
    }
}