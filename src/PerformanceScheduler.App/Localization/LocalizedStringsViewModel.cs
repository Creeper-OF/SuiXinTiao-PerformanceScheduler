using PerformanceScheduler.App.ViewModels;

namespace PerformanceScheduler.App.Localization;

public sealed class LocalizedStringsViewModel : ObservableObject, IDisposable
{
    private readonly JsonLocalizationService _localizationService;

    public LocalizedStringsViewModel(JsonLocalizationService localizationService)
    {
        _localizationService = localizationService;
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public string this[string key] => _localizationService.Get(key);

    public void Dispose()
    {
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged("Item[]");
    }
}
