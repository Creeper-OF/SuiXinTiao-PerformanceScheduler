namespace PerformanceScheduler.Core.Models;

public sealed record BackgroundProcessPolicy
{
    public string Category { get; init; } = "default";

    public PriorityLevel TargetPriority { get; init; } = PriorityLevel.BelowNormal;

    public bool PreferEfficiencyMode { get; init; }

    public string Notes { get; init; } = string.Empty;
}
