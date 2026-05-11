using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Gpu;

public abstract class PlaceholderGpuTuningProvider : IGpuTuningProvider
{
    protected PlaceholderGpuTuningProvider(GpuVendor vendor, string providerName)
    {
        Vendor = vendor;
        ProviderName = providerName;
    }

    public GpuVendor Vendor { get; }

    public string ProviderName { get; }

    public virtual Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GpuTuningProviderSupport
        {
            ProviderName = ProviderName,
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = false,
            SupportsClockLimit = false,
            SupportsVoltageControl = false,
            StatusKey = "Capability.GpuProviderPlaceholder"
        });
}
