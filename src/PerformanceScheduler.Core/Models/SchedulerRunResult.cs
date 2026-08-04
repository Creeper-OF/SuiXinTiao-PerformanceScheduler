namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerRunResult
{
    public Guid RunId { get; init; } = Guid.NewGuid();

    public Guid? TransactionId { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public FocusedAppContext? ActiveApp { get; init; }

    public PowerSourceMode PowerSourceMode { get; init; } = PowerSourceMode.Any;

    public ProfileMatchResult? MatchResult { get; init; }

    public PowerPlanInfo? ActivePowerPlan { get; init; }

    public PriorityAdjustmentResult? PriorityChange { get; init; }

    public IReadOnlyList<BackgroundProcessAdjustmentResult> BackgroundAdjustments { get; init; } =
        Array.Empty<BackgroundProcessAdjustmentResult>();

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();

    public MetricsSnapshot? Metrics { get; init; }

    public CapabilitySnapshot? Capabilities { get; init; }

    public bool Success { get; init; }

    public bool IsRetryDeferred { get; init; }

    public int ConsecutiveFailureCount { get; init; }

    public DateTimeOffset? RetryAfter { get; init; }

    public bool? FailureRecoveryCompleted { get; init; }

    public string Summary { get; init; } = string.Empty;
}
