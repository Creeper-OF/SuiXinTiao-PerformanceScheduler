namespace PerformanceScheduler.Core.Models;

public sealed record CpuSetPreference
{
    public bool Enabled { get; init; }

    public IReadOnlyList<uint>? ForegroundCpuSets { get; init; }

    public IReadOnlyList<uint>? BackgroundCpuSets { get; init; }
}