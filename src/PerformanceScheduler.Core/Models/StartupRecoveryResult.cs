namespace PerformanceScheduler.Core.Models;

public sealed record StartupRecoveryResult
{
    public bool PreviousSessionRecovered { get; init; }

    public bool RollbackApplied { get; init; }

    public bool LastProfileDisabled { get; init; }

    public bool SchedulingSuspended { get; init; }

    public string? LastProfileId { get; init; }

    public string? LastProfileName { get; init; }

    public int FailureCount { get; init; }

    public bool HasNotice => PreviousSessionRecovered || RollbackApplied || LastProfileDisabled;
}
