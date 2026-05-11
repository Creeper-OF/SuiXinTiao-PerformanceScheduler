using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Gpu;

public sealed class IntelGpuTuningProvider : PlaceholderGpuTuningProvider
{
    public IntelGpuTuningProvider()
        : base(GpuVendor.Intel, "Intel Arc Control Placeholder")
    {
    }
}
