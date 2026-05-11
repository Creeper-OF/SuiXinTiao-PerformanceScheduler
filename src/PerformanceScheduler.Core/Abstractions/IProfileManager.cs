using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IProfileManager
{
    Task<IReadOnlyList<PerformanceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default);

    Task SaveProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProfileRevisionInfo>> LoadProfileHistoryAsync(string profileId, CancellationToken cancellationToken = default);

    Task<PerformanceProfile?> RollbackProfileAsync(string profileId, string revisionId, CancellationToken cancellationToken = default);

    Task ExportProfilesAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task ImportProfilesAsync(string sourcePath, bool overwriteExisting, CancellationToken cancellationToken = default);
}
