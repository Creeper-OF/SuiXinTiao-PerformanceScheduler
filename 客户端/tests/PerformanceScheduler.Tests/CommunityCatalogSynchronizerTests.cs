using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Community;

namespace PerformanceScheduler.Tests;

public sealed class CommunityCatalogSynchronizerTests
{
    [Fact]
    public async Task SynchronizeAsync_WhenClientIsNull_ReturnsSkipped()
    {
        var profileCatalog = new MockProfileCatalog();
        var strategyCatalog = new MockStrategyCatalog();

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: null);

        var result = await synchronizer.SynchronizeAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRemoteAvailable);
        Assert.Contains("not configured", result.Message);
        Assert.False(profileCatalog.SaveCalled);
        Assert.False(strategyCatalog.SaveCalled);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenDisabledByPredicate_ReturnsSkipped()
    {
        var profileCatalog = new MockProfileCatalog();
        var strategyCatalog = new MockStrategyCatalog();
        var apiClient = new MockCommunityApiClient();

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: apiClient,
            isEnabledPredicate: () => false);

        var result = await synchronizer.SynchronizeAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRemoteAvailable);
        Assert.Contains("disabled", result.Message);
        Assert.False(apiClient.GetHealthCalled);
        Assert.False(profileCatalog.SaveCalled);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenHealthStatusIsNotOk_ReturnsFailedOffline()
    {
        var profileCatalog = new MockProfileCatalog();
        var strategyCatalog = new MockStrategyCatalog();
        var apiClient = new MockCommunityApiClient
        {
            HealthResult = new CommunityApiHealth { Status = "maintenance", Service = "api", ApiVersion = "v1" }
        };

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: apiClient,
            isEnabledPredicate: () => true);

        var result = await synchronizer.SynchronizeAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRemoteAvailable);
        Assert.Contains("health check failed", result.Message);
        Assert.False(profileCatalog.SaveCalled);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenSuccessful_SavesToBothCatalogs()
    {
        var profileCatalog = new MockProfileCatalog();
        var strategyCatalog = new MockStrategyCatalog();
        var apiClient = new MockCommunityApiClient
        {
            HealthResult = new CommunityApiHealth { Status = "ok", Service = "api", ApiVersion = "v1" },
            ProfilesResult =
            [
                new CommunityProfileEntry
                {
                    SchemaVersion = 1,
                    EntryId = "sync-p1",
                    Name = "Sync P1",
                    Profile = new PerformanceProfile { SchemaVersion = 1, Id = "sync-p1" }
                }
            ],
            StrategiesResult =
            [
                new CommunityDeviceStrategyEntry
                {
                    SchemaVersion = 1,
                    EntryId = "sync-s1",
                    Name = "Sync S1",
                    Package = new DeviceStrategyPackage { SchemaVersion = 1, PackageId = "pkg-1", Profiles = [] }
                }
            ]
        };

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: apiClient,
            isEnabledPredicate: () => true);

        var result = await synchronizer.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.IsRemoteAvailable);
        Assert.Equal(1, result.ProfilesCount);
        Assert.Equal(1, result.DeviceStrategiesCount);
        Assert.True(profileCatalog.SaveCalled);
        Assert.True(strategyCatalog.SaveCalled);
        Assert.Single(profileCatalog.SavedProfiles);
        Assert.Equal("sync-p1", profileCatalog.SavedProfiles[0].EntryId);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenRemoteThrowsException_ReturnsFailedOfflineWithoutCrashing()
    {
        var profileCatalog = new MockProfileCatalog();
        var strategyCatalog = new MockStrategyCatalog();
        var apiClient = new MockCommunityApiClient
        {
            ThrowOnHealth = true
        };

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: apiClient,
            isEnabledPredicate: () => true);

        var result = await synchronizer.SynchronizeAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRemoteAvailable);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(profileCatalog.SaveCalled);
    }

    [Fact]
    public async Task SynchronizeAsync_FiltersCorruptRemoteEntry_AndSavesOnlyValidEntries()
    {
        var profileCatalog = new MockProfileCatalog();
        var strategyCatalog = new MockStrategyCatalog();
        var apiClient = new MockCommunityApiClient
        {
            HealthResult = new CommunityApiHealth { Status = "ok" },
            ProfilesResult =
            [
                new CommunityProfileEntry
                {
                    SchemaVersion = 1,
                    EntryId = "valid-p",
                    Name = "Valid",
                    Profile = new PerformanceProfile { SchemaVersion = 1, Id = "valid-p" }
                },
                new CommunityProfileEntry
                {
                    SchemaVersion = 999, // Incompatible future schema
                    EntryId = "invalid-p",
                    Name = "Invalid",
                    Profile = new PerformanceProfile { SchemaVersion = 1, Id = "invalid-p" }
                }
            ],
            StrategiesResult = []
        };

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: apiClient,
            isEnabledPredicate: () => true);

        var result = await synchronizer.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.ProfilesCount);
        Assert.Single(profileCatalog.SavedProfiles);
        Assert.Equal("valid-p", profileCatalog.SavedProfiles[0].EntryId);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenStrategySaveFails_RollsBackProfileCatalog()
    {
        var initialProfiles = new List<CommunityProfileEntry>
        {
            new()
            {
                SchemaVersion = 1,
                EntryId = "existing-p0",
                Name = "Existing P0",
                Profile = new PerformanceProfile { SchemaVersion = 1, Id = "existing-p0" }
            }
        };

        var profileCatalog = new MockProfileCatalog(initialProfiles);
        var strategyCatalog = new MockStrategyCatalog { ThrowOnSave = true };
        var apiClient = new MockCommunityApiClient
        {
            HealthResult = new CommunityApiHealth { Status = "ok", Service = "api", ApiVersion = "v1" },
            ProfilesResult =
            [
                new CommunityProfileEntry
                {
                    SchemaVersion = 1,
                    EntryId = "new-p1",
                    Name = "New P1",
                    Profile = new PerformanceProfile { SchemaVersion = 1, Id = "new-p1" }
                }
            ],
            StrategiesResult =
            [
                new CommunityDeviceStrategyEntry
                {
                    SchemaVersion = 1,
                    EntryId = "new-s1",
                    Name = "New S1",
                    Package = new DeviceStrategyPackage { SchemaVersion = 1, PackageId = "pkg-1", Profiles = [] }
                }
            ]
        };

        var synchronizer = new CommunityCatalogSynchronizer(
            profileCatalog,
            strategyCatalog,
            apiClient: apiClient,
            isEnabledPredicate: () => true);

        var result = await synchronizer.SynchronizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(profileCatalog.SavedProfiles);
        Assert.Equal("existing-p0", profileCatalog.SavedProfiles[0].EntryId);
    }

    private sealed class MockProfileCatalog : ICommunityProfileCatalog
    {
        public MockProfileCatalog(IReadOnlyList<CommunityProfileEntry>? initial = null)
        {
            SavedProfiles = initial ?? [];
        }

        public string StoragePath => "C:\\fake\\community-catalog.json";
        public bool SaveCalled { get; private set; }
        public IReadOnlyList<CommunityProfileEntry> SavedProfiles { get; private set; }

        public Task<IReadOnlyList<CommunityProfileEntry>> LoadProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SavedProfiles);

        public Task SaveProfilesAsync(IReadOnlyList<CommunityProfileEntry> entries, CancellationToken cancellationToken = default)
        {
            SaveCalled = true;
            SavedProfiles = entries;
            return Task.CompletedTask;
        }
    }

    private sealed class MockStrategyCatalog : ICommunityDeviceStrategyCatalog
    {
        public string StoragePath => "C:\\fake\\device-strategy-catalog.json";
        public bool SaveCalled { get; private set; }
        public bool ThrowOnSave { get; set; }
        public IReadOnlyList<CommunityDeviceStrategyEntry> SavedStrategies { get; private set; } = [];

        public Task<IReadOnlyList<CommunityDeviceStrategyEntry>> LoadStrategiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SavedStrategies);

        public Task SaveStrategiesAsync(IReadOnlyList<CommunityDeviceStrategyEntry> entries, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("Simulated disk error during strategy catalog save.");
            }

            SaveCalled = true;
            SavedStrategies = entries;
            return Task.CompletedTask;
        }
    }

    private sealed class MockCommunityApiClient : ICommunityApiClient
    {
        public bool GetHealthCalled { get; private set; }
        public bool ThrowOnHealth { get; set; }
        public CommunityApiHealth? HealthResult { get; set; }
        public CommunityCatalogManifest? ManifestResult { get; set; }
        public IReadOnlyList<CommunityProfileEntry> ProfilesResult { get; set; } = [];
        public IReadOnlyList<CommunityDeviceStrategyEntry> StrategiesResult { get; set; } = [];

        public Task<CommunityApiHealth?> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            GetHealthCalled = true;
            if (ThrowOnHealth)
            {
                throw new CommunityApiException("Connection refused", "api/v1/health");
            }
            return Task.FromResult(HealthResult);
        }

        public Task<CommunityCatalogManifest?> GetManifestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ManifestResult);

        public Task<IReadOnlyList<CommunityProfileEntry>> FetchProfilesAsync(string? query = null, string? source = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProfilesResult);

        public Task<CommunityProfileEntry?> FetchProfileAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProfilesResult.FirstOrDefault(p => p.EntryId == entryId));

        public Task<IReadOnlyList<CommunityDeviceStrategyEntry>> FetchDeviceStrategiesAsync(string? query = null, string? source = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(StrategiesResult);

        public Task<CommunityDeviceStrategyEntry?> FetchDeviceStrategyAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(StrategiesResult.FirstOrDefault(s => s.EntryId == entryId));
    }
}
