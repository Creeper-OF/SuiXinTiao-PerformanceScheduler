namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.AmdAdlx;

public sealed class NullAmdGpuSource : IAmdGpuSource
{
    public Task<AmdGpuAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AmdGpuAvailability { FailureReason = "Unsupported OS platform or AMD driver not present." });

    public Task<AmdGpuSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AmdGpuSnapshot());

    public Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
