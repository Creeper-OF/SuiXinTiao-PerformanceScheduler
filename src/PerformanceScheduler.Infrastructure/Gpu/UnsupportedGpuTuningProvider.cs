using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Gpu;

public sealed class UnsupportedGpuTuningProvider : IGpuTuningProvider
{
    public GpuVendor Vendor => GpuVendor.Unknown;

    public string ProviderName => "Unavailable";

    public Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GpuTuningProviderSupport
        {
            ProviderName = ProviderName,
            SupportsVendorExtensions = false,
            SupportsApplyPipeline = false,
            SupportsClockLimit = false,
            SupportsVoltageControl = false,
            StatusKey = detectedGpu.HasDetectedAdapter
                ? "Capability.GpuVendorUnknown"
                : "Capability.GpuUnavailable"
        });
}
