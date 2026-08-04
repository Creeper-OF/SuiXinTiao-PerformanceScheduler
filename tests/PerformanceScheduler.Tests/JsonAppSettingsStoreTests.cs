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
    public void Update_PersistsNormalizedPresentMonExecutablePath()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var store = new JsonAppSettingsStore(settingsPath);

        store.Update(settings => settings.PresentMonExecutablePath = "  \"C:\\Tools\\PresentMon.exe\"  ");
        var reloadedStore = new JsonAppSettingsStore(settingsPath);

        Assert.Equal("C:\\Tools\\PresentMon.exe", reloadedStore.Current.PresentMonExecutablePath);
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

    [Theory]
    [InlineData(20, 120)]
    [InlineData(320, 320)]
    [InlineData(5000, 800)]
    public void Current_NormalizesUiMotionDuration(double configuredValue, double expectedValue)
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        File.WriteAllText(settingsPath, $$"""
        {
          "uiMotionDurationMilliseconds": {{configuredValue}}
        }
        """);

        var store = new JsonAppSettingsStore(settingsPath);

        Assert.Equal(expectedValue, store.Current.UiMotionDurationMilliseconds);
    }

    [Fact]
    public void Update_PersistsUiMotionPreferences()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var store = new JsonAppSettingsStore(settingsPath);

        store.Update(settings =>
        {
            settings.UiMotionEnabled = false;
            settings.UiMotionStaggerEnabled = false;
            settings.UiMotionDurationMilliseconds = 440;
        });
        var reloadedStore = new JsonAppSettingsStore(settingsPath);

        Assert.False(reloadedStore.Current.UiMotionEnabled);
        Assert.False(reloadedStore.Current.UiMotionStaggerEnabled);
        Assert.Equal(440, reloadedStore.Current.UiMotionDurationMilliseconds);
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
