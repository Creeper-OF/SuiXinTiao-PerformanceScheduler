using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IRollbackManager
{
    Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default);

    Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
