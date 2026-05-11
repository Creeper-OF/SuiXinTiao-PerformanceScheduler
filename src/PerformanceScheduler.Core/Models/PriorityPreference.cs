namespace PerformanceScheduler.Core.Models;

public sealed record PriorityPreference
{
    public PriorityLevel ForegroundPriority { get; init; } = PriorityLevel.AboveNormal;

    public bool LowerBackgroundProcesses { get; init; }

    public bool PreferEfficiencyModeForBackground { get; init; }
}
