using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Gpu;

public sealed class AmdGpuTuningProvider : PlaceholderGpuTuningProvider
{
    public AmdGpuTuningProvider()
        : base(GpuVendor.Amd, "AMD ADL Placeholder")
    {
    }
}
