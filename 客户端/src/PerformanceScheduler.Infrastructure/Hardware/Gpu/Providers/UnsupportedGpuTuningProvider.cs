using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public sealed class UnsupportedGpuTuningProvider : IGpuTuningProvider
{
    public GpuVendor Vendor => GpuVendor.Unknown;

    public string ProviderName => "Unavailable";

    public Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GpuTuningProviderSupport
        {
            ProviderName = ProviderName,
            SupportsVendorExtensions = false,
            SupportsApplyPipeline = false,
            StatusKey = detectedGpu.HasDetectedAdapter
                ? "Capability.GpuVendorUnknown"
                : "Capability.GpuUnavailable"
        });
    }
}
