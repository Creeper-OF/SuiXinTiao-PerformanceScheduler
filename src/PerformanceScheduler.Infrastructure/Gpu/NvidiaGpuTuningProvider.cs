using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Gpu;

public sealed class NvidiaGpuTuningProvider : PlaceholderGpuTuningProvider
{
    public NvidiaGpuTuningProvider()
        : base(GpuVendor.Nvidia, "NVIDIA NVAPI Placeholder")
    {
    }
}
