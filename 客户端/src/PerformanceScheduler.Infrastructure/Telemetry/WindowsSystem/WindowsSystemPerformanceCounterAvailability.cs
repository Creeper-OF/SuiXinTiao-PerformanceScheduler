namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

public sealed record WindowsSystemPerformanceCounterAvailability
{
    public bool SupportsCpuFrequency { get; init; }

    public bool SupportsCpuUtilization { get; init; }

    public bool SupportsThermalZoneTemperature { get; init; }

    public bool SupportsGpuUtilization { get; init; }

    public bool SupportsGpuMemory { get; init; }

    public bool IsSupported => SupportsCpuFrequency ||
                               SupportsCpuUtilization ||
                               SupportsThermalZoneTemperature ||
                               SupportsGpuUtilization ||
                               SupportsGpuMemory;
}
