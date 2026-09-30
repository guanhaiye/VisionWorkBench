using System.IO;
using System.Linq;
using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpOfflineExecutionPolicyTests
{
    [Fact]
    public void ConcurrentBatchesWithSameFrameSequence_GetDistinctPairedEvidencePaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vw-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pairs = new (string OriginalImagePath, string AnnotatedImagePath)[64];
            Parallel.For(0, pairs.Length, index =>
            {
                pairs[index] = EvidenceImagePathFactory.CreatePair(directory, frameSequence: 7);
                File.WriteAllText(pairs[index].OriginalImagePath, $"original-{index}");
                File.WriteAllText(pairs[index].AnnotatedImagePath, $"annotated-{index}");
            });

            Assert.Equal(pairs.Length, pairs.Select(pair => pair.OriginalImagePath).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(pairs.Length, pairs.Select(pair => pair.AnnotatedImagePath).Distinct(StringComparer.Ordinal).Count());
            Assert.All(pairs, pair =>
            {
                Assert.True(File.Exists(pair.OriginalImagePath));
                Assert.True(File.Exists(pair.AnnotatedImagePath));
                Assert.Equal(Path.GetFileName(pair.OriginalImagePath).Replace("-orig.png", "", StringComparison.Ordinal),
                    Path.GetFileName(pair.AnnotatedImagePath).Replace("-annot.png", "", StringComparison.Ordinal));
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void ProgressChanged_ReportsConcurrentRequestProgressUpdates()
    {
        const int updates = 64;
        var progress = new TcpExecutionProgress(updates);
        var notifications = 0;
        progress.ProgressChanged += _ => Interlocked.Increment(ref notifications);

        Parallel.For(0, updates, _ => progress.ReportProcessed());

        Assert.Equal(updates, notifications);
        Assert.Equal(updates, progress.Snapshot().ProcessedCount);
    }
    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(40)]
    public void BatchTimeout_ScalesWithSnapshotSize(int imageCount)
    {
        var timeout = TcpOfflineExecutionPolicy.CalculateTimeout(
            imageCount, TimeSpan.FromSeconds(30), measuredInferencePerImage: null);
        Assert.True(timeout > TimeSpan.FromSeconds(30));
        if (imageCount == 40) Assert.True(timeout > TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void MeasuredInferenceLatency_IncreasesBatchBudget()
    {
        var coldEstimate = TcpOfflineExecutionPolicy.CalculateTimeout(10, TimeSpan.FromSeconds(30), null);
        var measured = TcpOfflineExecutionPolicy.CalculateTimeout(10, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
        Assert.True(measured > coldEstimate);
    }

    [Theory]
    [InlineData(3, 0, "completed", 3, 0)]
    [InlineData(2, 1, "partial_failure", 2, 1)]
    public void RequestProgress_TracksCompletedRecordsAndSkippedImages(
        int processedCount, int skippedCount, string expectedStatus, int expectedProcessed, int expectedSkipped)
    {
        var requestProgress = new TcpExecutionProgress(3);
        var tracker = new TcpOfflineExecutionProgressTracker(requestProgress);
        for (var index = 0; index < processedCount; index++)
            tracker.ReportProcessed();
        requestProgress.SetSkippedCount(skippedCount);

        var result = TcpOfflineExecutionPolicy.CreateResult(requestProgress.Snapshot(), 3, "OK", 42);

        Assert.Equal(processedCount, tracker.ProcessedCount);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedProcessed, result.ProcessedCount);
        Assert.Equal(expectedSkipped, result.SkippedCount);
        Assert.Equal(3, result.TotalCount);
    }
    [Fact]
    public void BatchResult_IsPartialWhenAnyInputWasSkipped()
    {
        var progress = new TcpExecutionProgress(4);
        progress.ReportProcessed(3);
        progress.ReportSkipped(1);

        var result = TcpOfflineExecutionPolicy.CreateResult(progress.Snapshot(), 5, "OK", 42);
        Assert.Equal("partial_failure", result.Status);
        Assert.Equal(3, result.ProcessedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public void BatchResult_DoesNotCountUnprocessedImagesAsSuccess()
    {
        var progress = new TcpExecutionProgress(10);
        progress.ReportProcessed(3);

        var result = TcpOfflineExecutionPolicy.CreateResult(progress.Snapshot(), 3, "OK", 7);
        Assert.Equal("partial_failure", result.Status);
        Assert.Equal(3, result.ProcessedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(10, result.TotalCount);
    }
}
