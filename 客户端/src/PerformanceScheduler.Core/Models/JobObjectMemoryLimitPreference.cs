namespace PerformanceScheduler.Core.Models;

public sealed record JobObjectMemoryLimitPreference
{
    public bool Enabled { get; init; }

    public ulong? MaxCommitMemoryBytes { get; init; }
}
