namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.IntelLevelZero;

public sealed class NullIntelGpuSource : IIntelGpuSource
{
    public Task<IntelGpuAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IntelGpuAvailability { FailureReason = "Unsupported OS platform or Intel driver not present." });

    public Task<IntelGpuSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IntelGpuSnapshot());

    public Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
