using System.IO;

namespace VisionWorkbench.Application;

/// <summary>Creates a collision-resistant, paired path for one record's evidence images.</summary>
public static class EvidenceImagePathFactory
{
    public static (string OriginalImagePath, string AnnotatedImagePath) CreatePair(string directory, long frameSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var stem = $"{DateTimeOffset.UtcNow:HHmmssfff}-{frameSequence}-{Guid.NewGuid():N}";
        return (Path.Combine(directory, $"{stem}-orig.png"), Path.Combine(directory, $"{stem}-annot.png"));
    }
}