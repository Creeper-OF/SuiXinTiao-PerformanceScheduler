namespace PerformanceScheduler.Core.Models;

public sealed record PowerPlanPreference
{
    public Guid? SchemeGuid { get; init; }

    public string PreferredPlanName { get; init; } = string.Empty;

    public bool RestoreOnExit { get; init; } = true;

    public PowerPlanAdvancedPreference Advanced { get; init; } = new();
}
