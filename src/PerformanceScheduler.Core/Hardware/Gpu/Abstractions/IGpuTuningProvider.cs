using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IGpuTuningProvider
{
    GpuVendor Vendor { get; }

    string ProviderName { get; }

    Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default);
}
