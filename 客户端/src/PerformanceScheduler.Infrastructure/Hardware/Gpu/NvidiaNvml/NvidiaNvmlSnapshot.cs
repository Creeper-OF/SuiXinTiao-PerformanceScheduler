namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

public sealed record NvidiaNvmlSnapshot
{
    public double? UsagePercent { get; init; }

    public double? TemperatureCelsius { get; init; }

    public double? PowerWatts { get; init; }

    public double? GraphicsClockMegahertz { get; init; }

    public bool HasAnyData => UsagePercent is not null ||
                              TemperatureCelsius is not null ||
                              PowerWatts is not null ||
                              GraphicsClockMegahertz is not null;
}
