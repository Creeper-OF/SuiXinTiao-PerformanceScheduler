using System.Globalization;
using System.IO;
using System.Text.Json;
using PerformanceScheduler.App.Settings;

namespace PerformanceScheduler.App.Localization;

public sealed class JsonLocalizationService
{
    private readonly string _localesDirectory;
    private readonly JsonAppSettingsStore _settingsStore;
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    public JsonLocalizationService(string localesDirectory, JsonAppSettingsStore settingsStore)
    {
        _localesDirectory = localesDirectory;
        _settingsStore = settingsStore;
        LoadCatalogs();
        CurrentLanguageCode = ResolveInitialLanguage();
    }

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<LanguageOptionViewModel> AvailableLanguages { get; private set; } = Array.Empty<LanguageOptionViewModel>();

    public string CurrentLanguageCode { get; private set; } = "en-US";

    public string Get(string key)
    {
        if (_catalogs.TryGetValue(CurrentLanguageCode, out var activeCatalog) &&
            activeCatalog.TryGetValue(key, out var activeValue))
        {
            return activeValue;
        }

        if (_catalogs.TryGetValue("en-US", out var fallbackCatalog) &&
            fallbackCatalog.TryGetValue(key, out var fallbackValue))
        {
            return fallbackValue;
        }

        return $"[{key}]";
    }

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public void SetLanguage(string languageCode)
    {
        if (!_catalogs.ContainsKey(languageCode) ||
            string.Equals(CurrentLanguageCode, languageCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentLanguageCode = languageCode;
        _settingsStore.Update(settings => settings.LanguageCode = languageCode);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadCatalogs()
    {
        Directory.CreateDirectory(_localesDirectory);

        foreach (var path in Directory.GetFiles(_localesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var languageCode = Path.GetFileNameWithoutExtension(path);
            var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ??
                          new Dictionary<string, string>();
            _catalogs[languageCode] = catalog;
        }

        AvailableLanguages = _catalogs.Keys
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code => new LanguageOptionViewModel
            {
                Code = code,
                DisplayName = GetLanguageDisplayName(code)
            })
            .ToArray();
    }

    private string ResolveInitialLanguage()
    {
        var configuredLanguage = _settingsStore.Current.LanguageCode;
        if (!string.IsNullOrWhiteSpace(configuredLanguage) && _catalogs.ContainsKey(configuredLanguage))
        {
            return configuredLanguage;
        }

        var preferredLanguage = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";

        return _catalogs.ContainsKey(preferredLanguage)
            ? preferredLanguage
            : _catalogs.Keys.FirstOrDefault() ?? "en-US";
    }

    private static string GetLanguageDisplayName(string languageCode)
    {
        if (languageCode.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            return "简体中文";
        }

        return languageCode.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : languageCode;
    }
}
