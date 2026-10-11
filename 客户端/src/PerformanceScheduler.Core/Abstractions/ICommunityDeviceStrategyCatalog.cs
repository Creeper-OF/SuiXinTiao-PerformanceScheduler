using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICommunityDeviceStrategyCatalog
{
    string StoragePath { get; }

    Task<IReadOnlyList<CommunityDeviceStrategyEntry>> LoadStrategiesAsync(CancellationToken cancellationToken = default);

    Task SaveStrategiesAsync(IReadOnlyList<CommunityDeviceStrategyEntry> entries, CancellationToken cancellationToken = default);
}
