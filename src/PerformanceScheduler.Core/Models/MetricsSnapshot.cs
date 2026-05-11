namespace PerformanceScheduler.Core.Models;

public sealed record MetricsSnapshot
{
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;

    public double WorkingSetMb { get; init; }

    public int ThreadCount { get; init; }

    public TimeSpan TotalProcessorTime { get; init; }
}
