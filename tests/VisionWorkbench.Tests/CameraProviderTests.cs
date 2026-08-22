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
}
