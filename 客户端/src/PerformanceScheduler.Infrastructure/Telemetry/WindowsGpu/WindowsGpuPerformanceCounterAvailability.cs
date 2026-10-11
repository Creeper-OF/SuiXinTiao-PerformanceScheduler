namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

public sealed record WindowsGpuPerformanceCounterAvailability
{
    public bool SupportsEngineUtilization { get; init; }

    public bool SupportsProcessMemory { get; init; }

    public bool IsSupported => SupportsEngineUtilization || SupportsProcessMemory;
}
