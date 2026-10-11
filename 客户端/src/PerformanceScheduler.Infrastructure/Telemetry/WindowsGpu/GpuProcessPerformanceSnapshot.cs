namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

public sealed record GpuProcessPerformanceSnapshot
{
    public double? UsagePercent { get; init; }

    public double? DedicatedMemoryBytes { get; init; }

    public double? SharedMemoryBytes { get; init; }

    public bool HasAnyData => UsagePercent is not null ||
                              DedicatedMemoryBytes is not null ||
                              SharedMemoryBytes is not null;
}
