using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICommunityCatalogSynchronizer
{
    Task<CommunitySyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
}
