namespace PerformanceScheduler.App.ViewModels;

public sealed class ValueOptionViewModel<T>
{
    public required T Value { get; init; }

    public required string DisplayName { get; init; }
}
