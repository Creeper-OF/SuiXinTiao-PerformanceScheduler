using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IGpuTuningProvider
{
    GpuVendor Vendor { get; }

    string ProviderName { get; }

    Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<bool> TryResetLockedClocksAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
