using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IGpuTuningProviderRegistry
{
    IGpuTuningProvider Resolve(GpuVendor vendor);
}
