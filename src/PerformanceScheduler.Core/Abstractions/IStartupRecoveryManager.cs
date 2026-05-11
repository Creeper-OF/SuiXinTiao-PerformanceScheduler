using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IStartupRecoveryManager
{
    Task<StartupRecoveryResult> BeginSessionAsync(CancellationToken cancellationToken = default);

    Task TrackAppliedProfileAsync(PerformanceProfile? profile, CancellationToken cancellationToken = default);

    Task MarkCleanShutdownAsync(CancellationToken cancellationToken = default);
}
