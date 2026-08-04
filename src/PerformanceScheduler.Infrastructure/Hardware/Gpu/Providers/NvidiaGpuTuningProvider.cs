using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public sealed class NvidiaGpuTuningProvider : PlannedGpuTuningProvider
{
    public NvidiaGpuTuningProvider()
        : base(GpuVendor.Nvidia, "NVIDIA NVML")
    {
    }
}
