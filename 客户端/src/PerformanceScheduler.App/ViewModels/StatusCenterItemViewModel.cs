namespace PerformanceScheduler.App.ViewModels;

public sealed class StatusCenterItemViewModel : ObservableObject
{
    private DateTimeOffset _timestamp;
    private string _source = string.Empty;
    private string _message = string.Empty;
    private string _levelKey = string.Empty;
    private string _level = string.Empty;

    public DateTimeOffset Timestamp
    {
        get => _timestamp;
        set => SetProperty(ref _timestamp, value);
    }

    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string LevelKey
    {
        get => _levelKey;
        set => SetProperty(ref _levelKey, value);
    }

    public string Level
    {
        get => _level;
        set => SetProperty(ref _level, value);
    }
}
