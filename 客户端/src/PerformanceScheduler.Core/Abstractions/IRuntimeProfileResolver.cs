using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IRuntimeProfileResolver
{
    Task<DeviceStrategyRuntimeSelection> ResolveAsync(
        IReadOnlyList<PerformanceProfile> localProfiles,
        PowerSourceMode powerSourceMode,
        CancellationToken cancellationToken = default);
}
