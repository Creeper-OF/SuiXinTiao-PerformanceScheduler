namespace PerformanceScheduler.Core.Models;

public sealed record CapabilitySnapshot
{
    public bool SupportsPowerPlanSwitching { get; init; }

    public bool SupportsPriorityBoost { get; init; }

    public bool SupportsRollback { get; init; }

    public bool SupportsMetricsCollection { get; init; }

    public bool SupportsEfficiencyModeHint { get; init; }

    public GpuCapabilitySnapshot Gpu { get; init; } = new();

    public IReadOnlyList<PowerPlanInfo> AvailablePowerPlans { get; init; } = Array.Empty<PowerPlanInfo>();

    public IReadOnlyList<string> UnsupportedReasons { get; init; } = Array.Empty<string>();
}
