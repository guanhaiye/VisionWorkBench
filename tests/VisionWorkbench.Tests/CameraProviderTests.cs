using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Cameras.Files;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class CameraProviderTests
{
    [Fact]
    public async Task ImageFolder_MaxFrames_StopsAfterOneFrame()
    {
        Assert.True(Directory.Exists(TestPaths.SamplesStaticCount));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameCount = 0;
        var session = await new ImageFolderProvider().CreateSessionAsync(
            new CameraDescriptor
            {
                ProviderId = ImageFolderProvider.ProviderIdValue,
                DeviceId = TestPaths.SamplesStaticCount,
                DisplayName = TestPaths.SamplesStaticCount,
            }, CancellationToken.None);
        session.FrameReceived += (_, _) => Interlocked.Increment(ref frameCount);
        session.Completed += (_, _) => completed.TrySetResult();

        try
        {
            await session.OpenAsync(new CameraOpenOptions { MaxFrames = 1 }, CancellationToken.None);
            await session.StartAsync(CancellationToken.None);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(1, frameCount);
    }

    [Fact]
    public async Task ImageFolder_SnapshotReportsUnreadableFileAsSkipped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vw-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bad = Path.Combine(directory, "01-corrupt.png");
        await File.WriteAllTextAsync(bad, "not an image");
        var session = (IImageFolderCameraSession)await new ImageFolderProvider().CreateSessionAsync(
            new CameraDescriptor
            {
                ProviderId = ImageFolderProvider.ProviderIdValue,
                DeviceId = directory,
                DisplayName = directory,
            }, CancellationToken.None);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await session.OpenAsync(new CameraOpenOptions { ImageFiles = [bad] }, CancellationToken.None);
            Assert.Equal([bad], session.FileSnapshot);
            session.Completed += (_, _) => completed.TrySetResult();
            await session.StartAsync(CancellationToken.None);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, session.SkippedFileCount);
        }
        finally
        {
            await session.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public async Task ImageFolder_PauseStopsEmittingUntilResume()
    {
        Assert.True(Directory.Exists(TestPaths.SamplesStaticCount));
        var pausedAfterFirstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameCount = 0;
        var session = await new ImageFolderProvider().CreateSessionAsync(
            new CameraDescriptor
            {
                ProviderId = ImageFolderProvider.ProviderIdValue,
                DeviceId = TestPaths.SamplesStaticCount,
                DisplayName = TestPaths.SamplesStaticCount,
            }, CancellationToken.None);
        session.FrameReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref frameCount) == 1)
            {
                session.PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
                pausedAfterFirstFrame.TrySetResult();
            }
        };
        session.Completed += (_, _) => completed.TrySetResult();

        try
        {
            await session.OpenAsync(new CameraOpenOptions { FrameIntervalMs = 0 }, CancellationToken.None);
            await session.StartAsync(CancellationToken.None);
            await pausedAfterFirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            Assert.Equal(1, Volatile.Read(ref frameCount));

            await session.ResumeAsync(CancellationToken.None);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Volatile.Read(ref frameCount) > 1);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
