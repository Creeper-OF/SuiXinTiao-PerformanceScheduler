namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record ResourcePressureSummary
{
    public ResourcePressureLevel Level { get; init; }

    public double? SystemCpuUsagePercent { get; init; }

    public double? SystemGpuUsagePercent { get; init; }

    public bool HasData => SystemCpuUsagePercent is not null || SystemGpuUsagePercent is not null;
}
