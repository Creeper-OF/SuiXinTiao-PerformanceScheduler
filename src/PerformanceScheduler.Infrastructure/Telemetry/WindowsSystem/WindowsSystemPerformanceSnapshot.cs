namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

public sealed record WindowsSystemPerformanceSnapshot
{
    public double? CpuFrequencyMegahertz { get; init; }

    public double? ThermalZoneTemperatureCelsius { get; init; }

    public bool HasAnyData => CpuFrequencyMegahertz is not null ||
                              ThermalZoneTemperatureCelsius is not null;
}
