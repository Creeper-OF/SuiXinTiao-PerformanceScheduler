using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public sealed class AmdGpuTuningProvider : PlannedGpuTuningProvider
{
    public AmdGpuTuningProvider()
        : base(GpuVendor.Amd, "AMD ADLX")
    {
    }
}
