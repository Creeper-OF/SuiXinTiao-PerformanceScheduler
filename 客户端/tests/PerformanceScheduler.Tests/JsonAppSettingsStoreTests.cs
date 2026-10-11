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
    public void Current_ReportsCorruptSettingsWithoutOverwritingTheFile()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        const string corruptJson = "{ not valid settings";
        File.WriteAllText(settingsPath, corruptJson);

        var store = new JsonAppSettingsStore(settingsPath);

        Assert.Equal(2, store.Current.MonitoringIntervalSeconds);
        var issue = Assert.Single(store.LoadIssues);
        Assert.Equal(settingsPath, issue.SourcePath);
        Assert.Contains("safe defaults", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(corruptJson, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Current_ReportsSettingsDirectoryWithoutOverwritingIt()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings-directory");
        Directory.CreateDirectory(settingsPath);

        var store = new JsonAppSettingsStore(settingsPath);

        Assert.Single(store.LoadIssues);
        Assert.True(Directory.Exists(settingsPath));
    }

    [Fact]
    public void Update_ClearsPreviousSettingsLoadIssueAfterSuccessfulWrite()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        File.WriteAllText(settingsPath, "not json");
        var store = new JsonAppSettingsStore(settingsPath);

        Assert.NotEmpty(store.LoadIssues);
        store.Update(settings => settings.AutoStartMonitoringOnLaunch = true);

        Assert.Empty(store.LoadIssues);
        Assert.True(new JsonAppSettingsStore(settingsPath).Current.AutoStartMonitoringOnLaunch);
    }

    [Fact]
    public void ReloadFromDiskClearsLoadIssueAfterExternalRepair()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        File.WriteAllText(settingsPath, "not json");
        var store = new JsonAppSettingsStore(settingsPath);

        Assert.NotEmpty(store.LoadIssues);

        File.WriteAllText(settingsPath, "{\"autoStartMonitoringOnLaunch\":true}");
        store.ReloadFromDisk();

        Assert.Empty(store.LoadIssues);
        Assert.True(store.Current.AutoStartMonitoringOnLaunch);
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
    public async Task ConcurrentUpdates_PreserveSerializedSettingsChanges()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var store = new JsonAppSettingsStore(settingsPath);
        var updates = new Action<AppSettings>[]
        {
            settings => settings.PauseSchedulingAfterUnsafeExit = false,
            settings => settings.AutoRollbackOnUnsafeExit = false,
            settings => settings.DisableProfileAfterRepeatedUnsafeRuns = false,
            settings => settings.AutoStartMonitoringOnLaunch = true,
            settings => settings.LaunchAtStartup = true,
            settings => settings.LanguageTransitionAnimationEnabled = false,
            settings => settings.UiMotionEnabled = false,
            settings => settings.AppearanceBackgroundImageEnabled = true,
            settings => settings.AppearanceBackgroundMode = "split",
            settings => settings.UnsafeRunThreshold = 7,
            settings => settings.MonitoringIntervalSeconds = 5
        };

        await Task.WhenAll(updates.Select(update => Task.Run(() => store.Update(update))));

        Assert.False(store.Current.PauseSchedulingAfterUnsafeExit);
        Assert.False(store.Current.AutoRollbackOnUnsafeExit);
        Assert.False(store.Current.DisableProfileAfterRepeatedUnsafeRuns);
        Assert.True(store.Current.AutoStartMonitoringOnLaunch);
        Assert.True(store.Current.LaunchAtStartup);
        Assert.False(store.Current.LanguageTransitionAnimationEnabled);
        Assert.False(store.Current.UiMotionEnabled);
        Assert.True(store.Current.AppearanceBackgroundImageEnabled);
        Assert.Equal("split", store.Current.AppearanceBackgroundMode);
        Assert.Equal(7, store.Current.UnsafeRunThreshold);
        Assert.Equal(5, store.Current.MonitoringIntervalSeconds);
    }

    [Fact]
    public async Task SnapshotReadAndSynchronousUpdate_CanProgressTogether()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var store = new JsonAppSettingsStore(settingsPath);
        store.Update(settings => settings.AutoStartMonitoringOnLaunch = true);

        var snapshotTask = store.CaptureImportSnapshotAsync();
        var updateTask = Task.Run(() => store.Update(settings => settings.MonitoringIntervalSeconds = 5));

        await Task.WhenAll(snapshotTask, updateTask).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = await snapshotTask;

        Assert.NotEmpty(snapshot.Files);
        Assert.Equal(5, store.Current.MonitoringIntervalSeconds);
    }

    [Fact]
    public void Update_DoesNotCommitInMemoryWhenSettingsFileCannotBeWritten()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings-directory");
        Directory.CreateDirectory(settingsPath);
        var store = new JsonAppSettingsStore(settingsPath);

        Assert.ThrowsAny<Exception>(() =>
            store.Update(settings => settings.AutoStartMonitoringOnLaunch = true));

        Assert.False(store.Current.AutoStartMonitoringOnLaunch);
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
