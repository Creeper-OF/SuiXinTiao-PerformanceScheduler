namespace PerformanceScheduler.Core.Models;

public sealed record MetricsSnapshot
{
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;

    public double WorkingSetMb { get; init; }

    public double PrivateMemoryMb { get; init; }

    public int ThreadCount { get; init; }

    public int HandleCount { get; init; }

    public double? CpuUsagePercent { get; init; }

    public TimeSpan TotalProcessorTime { get; init; }

    public bool IsAvailable { get; init; } = true;
}
