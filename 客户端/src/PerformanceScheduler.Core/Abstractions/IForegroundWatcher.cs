using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IForegroundWatcher
{
    Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(CancellationToken cancellationToken = default);
}
