namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

public interface INvidiaNvmlSource
{
    Task<NvidiaNvmlAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<NvidiaNvmlSnapshot> CollectAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default);

    Task<bool> TryResetLockedClocksAsync(
        CancellationToken cancellationToken = default);
}
