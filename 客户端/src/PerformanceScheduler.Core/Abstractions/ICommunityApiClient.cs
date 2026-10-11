using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICommunityApiClient
{
    Task<CommunityApiHealth?> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<CommunityCatalogManifest?> GetManifestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommunityProfileEntry>> FetchProfilesAsync(
        string? query = null,
        string? source = null,
        CancellationToken cancellationToken = default);

    Task<CommunityProfileEntry?> FetchProfileAsync(
        string entryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommunityDeviceStrategyEntry>> FetchDeviceStrategiesAsync(
        string? query = null,
        string? source = null,
        CancellationToken cancellationToken = default);

    Task<CommunityDeviceStrategyEntry?> FetchDeviceStrategyAsync(
        string entryId,
        CancellationToken cancellationToken = default);
}
