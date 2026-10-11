namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerRunHistoryEntry
{
    public long StorageId { get; init; }

    public Guid? RunId { get; init; }

    public Guid? TransactionId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public Guid? PowerPlanGuid { get; init; }

    public string PowerPlanName { get; init; } = string.Empty;

    public string Priority { get; init; } = string.Empty;

    public bool Success { get; init; }

    public bool IsRetryDeferred { get; init; }

    public int ConsecutiveFailureCount { get; init; }

    public DateTimeOffset? RetryAfter { get; init; }

    public bool? FailureRecoveryCompleted { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();
}
