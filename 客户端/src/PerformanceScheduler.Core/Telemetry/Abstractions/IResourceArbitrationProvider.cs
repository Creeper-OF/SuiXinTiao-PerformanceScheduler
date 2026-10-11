using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Abstractions;

public interface IResourceArbitrationProvider
{
    Task<ResourceArbitrationSummary?> GetArbitrationSummaryAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);
}
