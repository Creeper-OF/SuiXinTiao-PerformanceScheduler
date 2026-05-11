namespace PerformanceScheduler.Core.Models;

public sealed record PowerPlanAdvancedPreference
{
    public int? ProcessorMaxStatePercent { get; init; }

    public bool HasChanges => ProcessorMaxStatePercent is not null;
}
