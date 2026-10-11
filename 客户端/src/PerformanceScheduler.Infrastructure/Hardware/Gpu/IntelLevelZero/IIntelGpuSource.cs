namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.IntelLevelZero;

public interface IIntelGpuSource
{
    Task<IntelGpuAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<IntelGpuSnapshot> CollectAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default);

    Task<bool> TryResetLockedClocksAsync(
        CancellationToken cancellationToken = default);
}
