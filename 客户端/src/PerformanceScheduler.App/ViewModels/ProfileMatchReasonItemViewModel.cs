namespace PerformanceScheduler.App.ViewModels;

public sealed record ProfileMatchReasonItemViewModel
{
    public required string Label { get; init; }

    public required string Detail { get; init; }
}
