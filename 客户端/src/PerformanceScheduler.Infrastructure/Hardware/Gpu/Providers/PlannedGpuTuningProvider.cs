using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public abstract class PlannedGpuTuningProvider : IGpuTuningProvider
{
    protected PlannedGpuTuningProvider(GpuVendor vendor, string providerName)
    {
        Vendor = vendor;
        ProviderName = providerName;
    }

    public GpuVendor Vendor { get; }

    public string ProviderName { get; }

    public virtual Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GpuTuningProviderSupport
        {
            ProviderName = ProviderName,
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = false,
            StatusKey = "Capability.GpuProviderPlaceholder"
        });
    }
}
