namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

public sealed record WindowsSystemPerformanceCounterAvailability
{
    public bool SupportsCpuFrequency { get; init; }

    public bool SupportsThermalZoneTemperature { get; init; }

    public bool IsSupported => SupportsCpuFrequency || SupportsThermalZoneTemperature;
}
