using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICapabilityDetector
{
    Task<CapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default);
}
