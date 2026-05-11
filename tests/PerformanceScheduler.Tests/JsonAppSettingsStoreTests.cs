using PerformanceScheduler.App.Settings;

namespace PerformanceScheduler.Tests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonAppSettingsStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceScheduler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Current_UsesDefaultMonitoringIntervalWhenSettingsFileDoesNotExist()
    {
        var store = new JsonAppSettingsStore(Path.Combine(_tempDirectory, "settings.json"));

        Assert.Equal(2, store.Current.MonitoringIntervalSeconds);
        Assert.False(store.Current.AutoStartMonitoringOnLaunch);
    }

    [Fact]
    public void Current_NormalizesUnsupportedMonitoringInterval()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        File.WriteAllText(settingsPath, """
        {
          "monitoringIntervalSeconds": 999
        }
        """);

        var store = new JsonAppSettingsStore(settingsPath);

        Assert.Equal(2, store.Current.MonitoringIntervalSeconds);
    }

    [Fact]
    public void Update_PersistsSupportedMonitoringInterval()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var store = new JsonAppSettingsStore(settingsPath);

        store.Update(settings => settings.MonitoringIntervalSeconds = 1);
        var reloadedStore = new JsonAppSettingsStore(settingsPath);

        Assert.Equal(1, reloadedStore.Current.MonitoringIntervalSeconds);
    }

    [Fact]
    public void Update_PersistsAutoStartMonitoringOnLaunch()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var store = new JsonAppSettingsStore(settingsPath);

        store.Update(settings => settings.AutoStartMonitoringOnLaunch = true);
        var reloadedStore = new JsonAppSettingsStore(settingsPath);

        Assert.True(reloadedStore.Current.AutoStartMonitoringOnLaunch);
    }

    [Fact]
    public void Current_NormalizesAppearanceColorHex()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        File.WriteAllText(settingsPath, """
        {
          "appearanceSurfaceColorHex": "fff4e8",
          "appearanceBorderColorHex": "not-a-color"
        }
        """);

        var store = new JsonAppSettingsStore(settingsPath);

        Assert.Equal("#FFF4E8", store.Current.AppearanceSurfaceColorHex);
        Assert.Equal(string.Empty, store.Current.AppearanceBorderColorHex);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
