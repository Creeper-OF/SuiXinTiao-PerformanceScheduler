using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IGpuCapabilityProvider
{
    Task<GpuCapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default);
}
