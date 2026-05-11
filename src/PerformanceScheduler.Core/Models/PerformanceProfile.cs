namespace PerformanceScheduler.Core.Models;

public sealed record PerformanceProfile
{
    public int SchemaVersion { get; init; } = 1;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "New Profile";

    public int Version { get; init; } = 1;

    public bool IsEnabled { get; init; } = true;

    public bool IsGlobalDefault { get; init; }

    public PowerSourceMode PowerSourceMode { get; init; } = PowerSourceMode.Any;

    public ProcessClassification TargetClassification { get; init; } = ProcessClassification.Unknown;

    public ProfileMatchCriteria Match { get; init; } = new();

    public PowerPlanPreference PowerPlan { get; init; } = new();

    public PriorityPreference Priority { get; init; } = new();

    public GpuPreference Gpu { get; init; } = new();

    public IReadOnlyList<BackgroundProcessPolicy> BackgroundPolicies { get; init; } = Array.Empty<BackgroundProcessPolicy>();

    public string Notes { get; init; } = string.Empty;
}
