using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public sealed class IntelGpuTuningProvider : PlannedGpuTuningProvider
{
    public IntelGpuTuningProvider()
        : base(GpuVendor.Intel, "Intel Level Zero Sysman")
    {
    }
}
