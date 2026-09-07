using Serilog.Events;
using VisionWorkbench.Infrastructure.Logging;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class LogPersistencePolicyTests
{
    [Fact]
    public void PersistenceLevelsFilterEventsAsConfigured()
    {
        var original = LogPersistencePolicy.Current;
        try
        {
            LogPersistencePolicy.Current = LogPersistenceLevel.All;
            Assert.True(LogPersistencePolicy.ShouldPersist(LogEventLevel.Verbose));

            LogPersistencePolicy.Current = LogPersistenceLevel.CriticalAndAbove;
            Assert.False(LogPersistencePolicy.ShouldPersist(LogEventLevel.Error));
            Assert.True(LogPersistencePolicy.ShouldPersist(LogEventLevel.Fatal));

            LogPersistencePolicy.Current = LogPersistenceLevel.InformationAndAbove;
            Assert.False(LogPersistencePolicy.ShouldPersist(LogEventLevel.Debug));
            Assert.True(LogPersistencePolicy.ShouldPersist(LogEventLevel.Information));

            LogPersistencePolicy.Current = LogPersistenceLevel.WarningAndAbove;
            Assert.False(LogPersistencePolicy.ShouldPersist(LogEventLevel.Information));
            Assert.True(LogPersistencePolicy.ShouldPersist(LogEventLevel.Warning));

            LogPersistencePolicy.Current = LogPersistenceLevel.None;
            Assert.False(LogPersistencePolicy.ShouldPersist(LogEventLevel.Fatal));
        }
        finally
        {
            LogPersistencePolicy.Current = original;
        }
    }
}
