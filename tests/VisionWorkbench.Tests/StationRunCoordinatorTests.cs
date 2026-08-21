using VisionWorkbench.Application;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class StationRunCoordinatorTests
{
    [Fact]
    public async Task StartAll_Reports_Disabled_Station_Without_Starting_Resources()
    {
        await using var coordinator = new StationRunCoordinator(null!, null!, null!);
        var results = await coordinator.StartAllAsync(
        [
            new StationStartRequest
            {
                Station = new StationEntity
                {
                    Id = 11,
                    StationCode = "ST-011",
                    Enabled = false,
                },
                ProjectId = "project-a",
                PhysicalDeviceKey = "usb:11",
                TaskId = 1,
                Recipe = null!,
                Camera = null!,
                Algorithm = null!,
            },
        ]);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Contains("未启用", result.Error);
        Assert.Empty(coordinator.RunningStationIds);
    }
}
