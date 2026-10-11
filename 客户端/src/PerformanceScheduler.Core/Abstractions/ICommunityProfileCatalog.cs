using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICommunityProfileCatalog
{
    string StoragePath { get; }

    Task<IReadOnlyList<CommunityProfileEntry>> LoadProfilesAsync(CancellationToken cancellationToken = default);

    Task SaveProfilesAsync(IReadOnlyList<CommunityProfileEntry> entries, CancellationToken cancellationToken = default);
}
