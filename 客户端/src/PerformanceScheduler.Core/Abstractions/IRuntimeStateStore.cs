using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IRuntimeStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchedulerRunHistoryEntry>> LoadRecentRunsAsync(
        int count = 50,
        CancellationToken cancellationToken = default);
}
