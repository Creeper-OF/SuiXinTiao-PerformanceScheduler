namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

public sealed record WindowsSystemPerformanceSnapshot
{
    public double? CpuFrequencyMegahertz { get; init; }

    public double? CpuUsagePercent { get; init; }

    public double? ThermalZoneTemperatureCelsius { get; init; }

    public double? GpuUsagePercent { get; init; }

    public double? GpuDedicatedMemoryBytes { get; init; }

    public double? GpuSharedMemoryBytes { get; init; }

    public bool HasAnyData => CpuFrequencyMegahertz is not null ||
                              CpuUsagePercent is not null ||
                              ThermalZoneTemperatureCelsius is not null ||
                              GpuUsagePercent is not null ||
                              GpuDedicatedMemoryBytes is not null ||
                              GpuSharedMemoryBytes is not null;
}
