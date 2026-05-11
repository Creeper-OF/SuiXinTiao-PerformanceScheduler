namespace PerformanceScheduler.Core.Models;

public sealed record PowerPlanAdvancedState
{
    public Guid SchemeGuid { get; init; }

    public PowerSourceMode PowerSourceMode { get; init; } = PowerSourceMode.Any;

    public int? ProcessorMaxStatePercent { get; init; }
}
