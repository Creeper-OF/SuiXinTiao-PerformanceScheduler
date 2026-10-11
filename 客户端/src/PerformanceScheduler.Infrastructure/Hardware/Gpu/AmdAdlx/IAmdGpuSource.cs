namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.AmdAdlx;

public interface IAmdGpuSource
{
    Task<AmdGpuAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<AmdGpuSnapshot> CollectAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default);

    Task<bool> TryResetLockedClocksAsync(
        CancellationToken cancellationToken = default);
}
