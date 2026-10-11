using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Abstractions;

public interface IResourceArbitrationCandidateProvider
{
    Task<IReadOnlyList<ResourceArbitrationCandidate>> CollectAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile? profile = null,
        CancellationToken cancellationToken = default);
}
