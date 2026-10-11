using PerformanceScheduler.App.Services;

namespace PerformanceScheduler.Tests;

public sealed class ConfigurationRecoveryMarkerStoreTests : IDisposable
{
    private readonly string _runtimeDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    public ConfigurationRecoveryMarkerStoreTests()
    {
        Directory.CreateDirectory(_runtimeDirectory);
    }

    [Fact]
    public void MarkAndClear_PersistsManualRecoveryBoundary()
    {
        var marker = new ConfigurationRecoveryMarkerStore(_runtimeDirectory);

        Assert.False(marker.IsMarked);

        marker.Mark();

        Assert.True(marker.IsMarked);
        Assert.True(new ConfigurationRecoveryMarkerStore(_runtimeDirectory).IsMarked);
        Assert.True(marker.TryClear(out var failure));
        Assert.Null(failure);
        Assert.False(marker.IsMarked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeDirectory))
        {
            Directory.Delete(_runtimeDirectory, recursive: true);
        }
    }
}
