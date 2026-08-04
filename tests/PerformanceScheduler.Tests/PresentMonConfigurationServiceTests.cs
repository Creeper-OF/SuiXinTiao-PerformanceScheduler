using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

namespace PerformanceScheduler.Tests;

public sealed class PresentMonConfigurationServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryConfigure_WithConsoleExecutable_PersistsAndResolvesPath()
    {
        var fixture = CreateFixture();
        var executablePath = CreateFile(Path.Combine(_tempDirectory, "PresentMon-2.4.1-x64.exe"));

        var configured = fixture.Service.TryConfigure(executablePath);
        var state = fixture.Service.GetState();

        Assert.True(configured);
        Assert.Equal(Path.GetFullPath(executablePath), fixture.Store.Current.PresentMonExecutablePath);
        Assert.Equal(PresentMonConfigurationStatus.Configured, state.Status);
        Assert.Equal(Path.GetFullPath(executablePath), state.ResolvedPath);
    }

    [Fact]
    public void TryConfigure_WithUnrelatedExecutable_DoesNotReplaceCurrentConfiguration()
    {
        var fixture = CreateFixture();
        var validPath = CreateFile(Path.Combine(_tempDirectory, "PresentMon.exe"));
        var invalidPath = CreateFile(Path.Combine(_tempDirectory, "Other.exe"));
        Assert.True(fixture.Service.TryConfigure(validPath));

        var configured = fixture.Service.TryConfigure(invalidPath);

        Assert.False(configured);
        Assert.Equal(Path.GetFullPath(validPath), fixture.Store.Current.PresentMonExecutablePath);
    }

    [Fact]
    public void ClearConfiguration_RemovesOnlyExplicitPath()
    {
        var fixture = CreateFixture();
        var executablePath = CreateFile(Path.Combine(_tempDirectory, "PresentMon.exe"));
        Assert.True(fixture.Service.TryConfigure(executablePath));

        fixture.Service.ClearConfiguration();

        Assert.Null(fixture.Store.Current.PresentMonExecutablePath);
    }

    private (JsonAppSettingsStore Store, PresentMonConfigurationService Service) CreateFixture()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new JsonAppSettingsStore(Path.Combine(_tempDirectory, "settings.json"));
        var locator = new PresentMonExecutableLocator(
            _tempDirectory,
            _ => null,
            () => store.Current.PresentMonExecutablePath);
        return (store, new PresentMonConfigurationService(store, locator));
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
