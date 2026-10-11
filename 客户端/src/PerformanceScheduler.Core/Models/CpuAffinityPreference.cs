namespace PerformanceScheduler.Core.Models;

public sealed record CpuAffinityPreference
{
    public bool Enabled { get; init; }

    public long? ForegroundAffinityMask { get; init; }

    public long? BackgroundAffinityMask { get; init; }
}
