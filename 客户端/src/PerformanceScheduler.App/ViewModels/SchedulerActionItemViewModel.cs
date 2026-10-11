namespace PerformanceScheduler.App.ViewModels;

public sealed class SchedulerActionItemViewModel
{
    public DateTimeOffset CompletedAt { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string ValueChange { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;

    public string TechnicalMessage { get; init; } = string.Empty;

    public string Recovery { get; init; } = string.Empty;
}
