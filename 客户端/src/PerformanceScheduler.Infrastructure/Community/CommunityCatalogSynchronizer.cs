using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Community;

public sealed class CommunityCatalogSynchronizer : ICommunityCatalogSynchronizer
{
    private readonly ICommunityApiClient? _apiClient;
    private readonly ICommunityProfileCatalog _profileCatalog;
    private readonly ICommunityDeviceStrategyCatalog _deviceStrategyCatalog;
    private readonly IAppLogger? _logger;
    private readonly Func<bool>? _isEnabledPredicate;

    public CommunityCatalogSynchronizer(
        ICommunityProfileCatalog profileCatalog,
        ICommunityDeviceStrategyCatalog deviceStrategyCatalog,
        ICommunityApiClient? apiClient = null,
        IAppLogger? logger = null,
        Func<bool>? isEnabledPredicate = null)
    {
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _deviceStrategyCatalog = deviceStrategyCatalog ?? throw new ArgumentNullException(nameof(deviceStrategyCatalog));
        _apiClient = apiClient;
        _logger = logger;
        _isEnabledPredicate = isEnabledPredicate;
    }

    public async Task<CommunitySyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (_apiClient is null)
        {
            return CommunitySyncResult.Skipped("Remote community API client is not configured.");
        }

        if (_isEnabledPredicate is not null && !_isEnabledPredicate())
        {
            return CommunitySyncResult.Skipped("Remote community synchronization is disabled by configuration.");
        }

        try
        {
            _logger?.Info("Synchronizing community catalog from remote server...");

            var health = await _apiClient.GetHealthAsync(cancellationToken);
            if (health is null || !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var statusMsg = health is null ? "no health response" : $"status: {health.Status}";
                _logger?.Warn($"Community server health check failed ({statusMsg}). Falling back to local catalog.");
                return CommunitySyncResult.FailedOffline($"Community server health check failed ({statusMsg}).");
            }

            var remoteProfiles = await _apiClient.FetchProfilesAsync(cancellationToken: cancellationToken);
            var remoteStrategies = await _apiClient.FetchDeviceStrategiesAsync(cancellationToken: cancellationToken);

            var validProfiles = new List<CommunityProfileEntry>();
            foreach (var profileEntry in remoteProfiles)
            {
                if (profileEntry is null)
                {
                    continue;
                }

                try
                {
                    validProfiles.Add(ConfigurationSchemaPolicy.ValidateCommunityProfileEntry(
                        profileEntry,
                        $"remote:profiles#{profileEntry.EntryId}"));
                }
                catch (Exception ex) when (ex is UnsupportedConfigurationSchemaException or InvalidProfileConfigurationException or ArgumentException)
                {
                    _logger?.Warn($"Remote community profile '{profileEntry.EntryId}' skipped due to validation: {ex.Message}");
                }
            }

            var validStrategies = new List<CommunityDeviceStrategyEntry>();
            foreach (var strategyEntry in remoteStrategies)
            {
                if (strategyEntry is null)
                {
                    continue;
                }

                try
                {
                    validStrategies.Add(ConfigurationSchemaPolicy.ValidateCommunityDeviceStrategyEntry(
                        strategyEntry,
                        $"remote:device-strategies#{strategyEntry.EntryId}"));
                }
                catch (Exception ex) when (ex is UnsupportedConfigurationSchemaException or InvalidProfileConfigurationException or ArgumentException)
                {
                    _logger?.Warn($"Remote community device strategy '{strategyEntry.EntryId}' skipped due to validation: {ex.Message}");
                }
            }

            var previousProfiles = await _profileCatalog.LoadProfilesAsync(cancellationToken);

            await _profileCatalog.SaveProfilesAsync(validProfiles, cancellationToken);
            try
            {
                await _deviceStrategyCatalog.SaveStrategiesAsync(validStrategies, cancellationToken);
            }
            catch
            {
                try
                {
                    await _profileCatalog.SaveProfilesAsync(previousProfiles, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    _logger?.Warn($"Failed to roll back community profile catalog after device strategy save failure: {rollbackEx.Message}");
                }

                throw;
            }

            _logger?.Info($"Community catalog synchronized: {validProfiles.Count} profiles, {validStrategies.Count} device strategies.");
            return CommunitySyncResult.Success(validProfiles.Count, validStrategies.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is CommunityApiException or HttpRequestException or IOException)
        {
            _logger?.Warn($"Community catalog synchronization failed: {exception.Message}. Using local cache.");
            return CommunitySyncResult.FailedOffline("Community server is currently unavailable.", exception);
        }
    }
}
