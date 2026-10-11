namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record ResourceContentionSummary
{
    public ResourceContentionLevel Level { get; init; }

    public double? ForegroundCpuUsagePercent { get; init; }

    public double? SystemCpuUsagePercent { get; init; }

    public double? ForegroundGpuUsagePercent { get; init; }

    public double? SystemGpuUsagePercent { get; init; }

    public bool HasComparison =>
        ForegroundCpuUsagePercent is not null && SystemCpuUsagePercent is not null ||
        ForegroundGpuUsagePercent is not null && SystemGpuUsagePercent is not null;
}
