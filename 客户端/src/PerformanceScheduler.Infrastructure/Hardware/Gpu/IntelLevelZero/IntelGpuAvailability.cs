namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.IntelLevelZero;

public sealed record IntelGpuAvailability
{
    public bool IsAvailable { get; init; }

    public int DeviceCount { get; init; }

    public bool SupportsClockLocking { get; init; }

    public string FailureReason { get; init; } = string.Empty;
}
