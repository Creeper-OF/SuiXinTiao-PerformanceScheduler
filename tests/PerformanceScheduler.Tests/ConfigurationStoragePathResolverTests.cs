using PerformanceScheduler.App.Services;

namespace PerformanceScheduler.Tests;

public sealed class ConfigurationStoragePathResolverTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_UsesApplicationDirectoryWhenItIsWritable()
    {
        var applicationDirectory = Path.Combine(_tempDirectory, "portable");
        var applicationDataRoot = Path.Combine(_tempDirectory, "local");

        var result = ConfigurationStoragePathResolver.Resolve(applicationDirectory, applicationDataRoot);

        Assert.True(result.IsPortable);
        Assert.Equal(Path.Combine(applicationDirectory, "profiles"), result.ProfilesDirectory);
        Assert.Equal(Path.Combine(applicationDirectory, "device-strategies"), result.DeviceStrategiesDirectory);
    }

    [Fact]
    public void Resolve_FallsBackAndSeedsProfilesWhenApplicationDirectoryIsNotWritable()
    {
        Directory.CreateDirectory(_tempDirectory);
        var applicationPath = Path.Combine(_tempDirectory, "application-file");
        File.WriteAllText(applicationPath, "not a directory");
        var applicationDataRoot = Path.Combine(_tempDirectory, "local");

        var result = ConfigurationStoragePathResolver.Resolve(applicationPath, applicationDataRoot);

        Assert.False(result.IsPortable);
        Assert.Equal(Path.Combine(applicationDataRoot, "configuration", "profiles"), result.ProfilesDirectory);
        Assert.True(Directory.Exists(result.ProfilesDirectory));
        Assert.True(Directory.Exists(result.DeviceStrategiesDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
