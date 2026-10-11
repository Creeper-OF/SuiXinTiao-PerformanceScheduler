using PerformanceScheduler.App.Localization;
using PerformanceScheduler.App.Settings;

namespace PerformanceScheduler.Tests;

public sealed class JsonLocalizationServiceTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        $"settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void AvailableLanguages_UsesReadableChineseAndEnglishNames()
    {
        var service = CreateService();

        var chinese = Assert.Single(service.AvailableLanguages, language => language.Code == "zh-CN");
        var english = Assert.Single(service.AvailableLanguages, language => language.Code == "en-US");

        Assert.Equal("简体中文", chinese.DisplayName);
        Assert.Equal("English", english.DisplayName);
    }

    [Fact]
    public void InitialLanguage_FallsBackToSupportedCatalog()
    {
        var service = CreateService();

        Assert.Contains(
            service.CurrentLanguageCode,
            service.AvailableLanguages.Select(language => language.Code));
    }

    [Fact]
    public void SetLanguage_DoesNotCommitInMemoryWhenSettingsCannotBeWritten()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            "PerformanceScheduler.Tests",
            $"settings-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsPath);
        try
        {
            var service = new JsonLocalizationService(
                Path.Combine(AppContext.BaseDirectory, "locales"),
                new JsonAppSettingsStore(settingsPath));
            var originalLanguage = service.CurrentLanguageCode;
            var targetLanguage = originalLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "zh-CN";

            Assert.ThrowsAny<Exception>(() => service.SetLanguage(targetLanguage));

            Assert.Equal(originalLanguage, service.CurrentLanguageCode);
        }
        finally
        {
            if (Directory.Exists(settingsPath))
            {
                Directory.Delete(settingsPath, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadCatalogs_SkipsBrokenOptionalCatalog()
    {
        var localesDirectory = Path.Combine(
            Path.GetTempPath(),
            "PerformanceScheduler.Tests",
            $"locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localesDirectory);
        try
        {
            File.WriteAllText(Path.Combine(localesDirectory, "zh-CN.json"), "{ \"Language.Name\": \"简体中文\" }");
            File.WriteAllText(Path.Combine(localesDirectory, "en-US.json"), "{ \"Language.Name\": \"English\" }");
            File.WriteAllText(Path.Combine(localesDirectory, "broken.json"), "{ not valid json");

            var service = new JsonLocalizationService(
                localesDirectory,
                new JsonAppSettingsStore(_settingsPath));

            Assert.Equal(
                new[] { "en-US", "zh-CN" },
                service.AvailableLanguages.Select(language => language.Code));
        }
        finally
        {
            if (Directory.Exists(localesDirectory))
            {
                Directory.Delete(localesDirectory, recursive: true);
            }
        }
    }

    private JsonLocalizationService CreateService()
    {
        var settingsStore = new JsonAppSettingsStore(_settingsPath);
        return new JsonLocalizationService(
            Path.Combine(AppContext.BaseDirectory, "locales"),
            settingsStore);
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
    }
}
