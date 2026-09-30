using VisionWorkbench.Application.Communication;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpOfflineExecutionPolicyTests
{
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
