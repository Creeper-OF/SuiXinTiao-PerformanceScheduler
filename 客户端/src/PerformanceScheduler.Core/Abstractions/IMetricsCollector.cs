using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IMetricsCollector
{
    Task<MetricsSnapshot> CollectAsync(FocusedAppContext app, CancellationToken cancellationToken = default);
}
