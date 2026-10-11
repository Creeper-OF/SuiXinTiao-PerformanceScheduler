using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerRunResult
{
    public Guid RunId { get; init; } = Guid.NewGuid();

    public Guid? TransactionId { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public FocusedAppContext? ActiveApp { get; init; }

    public PowerSourceMode PowerSourceMode { get; init; } = PowerSourceMode.Any;

    public ProfileMatchResult? MatchResult { get; init; }

    // Runtime-only context. The SQLite history store intentionally keeps its
    // existing schema and persists the selected Profile, not package metadata.
    public DeviceStrategyRuntimeSelection? DeviceStrategySelection { get; init; }

    public PowerPlanInfo? ActivePowerPlan { get; init; }

    public PriorityAdjustmentResult? PriorityChange { get; init; }

    public IReadOnlyList<BackgroundProcessAdjustmentResult> BackgroundAdjustments { get; init; } =
        Array.Empty<BackgroundProcessAdjustmentResult>();

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();

    public MetricsSnapshot? Metrics { get; init; }

    public CapabilitySnapshot? Capabilities { get; init; }

    public ResourceArbitrationSummary? ResourceArbitration { get; init; }

    public TargetExperienceAdaptiveResult? TargetExperienceAdaptation { get; init; }

    public bool Success { get; init; }

    public bool IsRetryDeferred { get; init; }

    public int ConsecutiveFailureCount { get; init; }

    public DateTimeOffset? RetryAfter { get; init; }

    public bool? FailureRecoveryCompleted { get; init; }

    // A successful provider run was rolled back because its runtime history
    // could not be persisted safely.
    public bool RuntimeStatePersistenceFailed { get; init; }

    public string Summary { get; init; } = string.Empty;
}
