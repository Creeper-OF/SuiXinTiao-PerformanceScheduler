using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;
using PerformanceScheduler.Infrastructure.Safety;

namespace PerformanceScheduler.Tests;

public sealed class FileStartupRecoveryManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _profilesDirectory;
    private readonly string _runtimeDirectory;

    public FileStartupRecoveryManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceScheduler.Tests", Guid.NewGuid().ToString("N"));
        _profilesDirectory = Path.Combine(_tempDirectory, "profiles");
        _runtimeDirectory = Path.Combine(_tempDirectory, "runtime");
        Directory.CreateDirectory(_profilesDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
    }

    [Fact]
    public async Task BeginSessionAsync_DisablesProfileAfterRepeatedUnsafeRuns()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "unsafe-profile",
            Name = "Unsafe Profile",
            Version = 1,
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "unsafe" } }
        };

        await profileManager.SaveProfileAsync(profile);

        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);

        rollbackManager.LastState = new SchedulerState();
        var manager2 = CreateManager(profileManager, rollbackManager, logger);
        var firstRecovery = await manager2.BeginSessionAsync();

        Assert.True(firstRecovery.PreviousSessionRecovered);
        Assert.True(firstRecovery.RollbackApplied);
        Assert.False(firstRecovery.LastProfileDisabled);
        Assert.Equal(1, firstRecovery.FailureCount);

        await manager2.TrackAppliedProfileAsync(profile);
        rollbackManager.LastState = new SchedulerState();

        var manager3 = CreateManager(profileManager, rollbackManager, logger);
        var secondRecovery = await manager3.BeginSessionAsync();
        var savedProfile = Assert.Single(await profileManager.LoadProfilesAsync());

        Assert.True(secondRecovery.LastProfileDisabled);
        Assert.Equal(2, secondRecovery.FailureCount);
        Assert.False(savedProfile.IsEnabled);
        Assert.Equal(2, savedProfile.Version);
    }

    [Fact]
    public async Task MarkCleanShutdownAsync_ResetsUnsafeRunCounter()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "stable-profile",
            Name = "Stable Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "stable" } }
        };

        await profileManager.SaveProfileAsync(profile);

        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);

        rollbackManager.LastState = new SchedulerState();
        var manager2 = CreateManager(profileManager, rollbackManager, logger);
        var firstRecovery = await manager2.BeginSessionAsync();
        Assert.Equal(1, firstRecovery.FailureCount);

        await manager2.TrackAppliedProfileAsync(profile);
        await manager2.MarkCleanShutdownAsync();

        var manager3 = CreateManager(profileManager, rollbackManager, logger);
        await manager3.BeginSessionAsync();
        await manager3.TrackAppliedProfileAsync(profile);

        rollbackManager.LastState = new SchedulerState();
        var manager4 = CreateManager(profileManager, rollbackManager, logger);
        var recoveryAfterCleanExit = await manager4.BeginSessionAsync();
        var savedProfile = Assert.Single(await profileManager.LoadProfilesAsync());

        Assert.False(recoveryAfterCleanExit.LastProfileDisabled);
        Assert.Equal(1, recoveryAfterCleanExit.FailureCount);
        Assert.True(savedProfile.IsEnabled);
    }

    [Fact]
    public async Task TrackAppliedProfileAsync_ClearsPreviousProfileWhenNoProfileIsActive()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "cleared-profile",
            Name = "Cleared Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "cleared" } }
        };

        await profileManager.SaveProfileAsync(profile);
        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);
        await manager1.TrackAppliedProfileAsync(null);

        var manager2 = CreateManager(profileManager, rollbackManager, logger);
        var recovery = await manager2.BeginSessionAsync();

        Assert.True(recovery.PreviousSessionRecovered);
        Assert.Equal(0, recovery.FailureCount);
        Assert.Null(recovery.LastProfileId);
        Assert.False(recovery.LastProfileDisabled);
        Assert.True((await profileManager.LoadProfilesAsync()).Single().IsEnabled);
    }

    [Fact]
    public async Task BeginSessionAsync_SuspendsSchedulingWhenStartupStateIsCorrupt()
    {
        await File.WriteAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json"), "{ not valid json");
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var manager = CreateManager(profileManager, rollbackManager, logger);

        var result = await manager.BeginSessionAsync();

        Assert.False(result.PreviousSessionRecovered);
        Assert.True(result.StartupStateReadFailed);
        Assert.True(result.SchedulingSuspended);
        Assert.Contains(
            logger.GetRecentEntries(),
            entry => entry.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "{ not valid json",
            await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json")));
    }

    [Fact]
    public async Task BeginSessionAsync_RecoversPendingRollbackWhenStartupSessionIsMissing()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager { LastState = new SchedulerState() };
        var logger = new TestLogger();
        var manager = CreateManager(profileManager, rollbackManager, logger);

        var result = await manager.BeginSessionAsync();

        Assert.False(result.PreviousSessionRecovered);
        Assert.True(result.RollbackApplied);
        Assert.False(result.RollbackFailed);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Null(rollbackManager.LastState);
    }

    [Fact]
    public async Task BeginSessionAsync_SuspendsSchedulingWhenAutomaticRollbackIsDisabled()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager { LastState = new SchedulerState() };
        var logger = new TestLogger();
        var manager = CreateManager(
            profileManager,
            rollbackManager,
            logger,
            pauseSchedulingAfterUnsafeExit: false,
            autoRollbackOnUnsafeExit: false);

        var result = await manager.BeginSessionAsync();

        Assert.True(result.RollbackPending);
        Assert.True(result.SchedulingSuspended);
        Assert.False(result.RollbackApplied);
        Assert.Equal(0, rollbackManager.RollbackCallCount);
    }

    [Fact]
    public async Task BeginSessionAsync_SuspendsWhenRepeatedUnsafeProfileCannotBeDisabled()
    {
        var profile = new PerformanceProfile
        {
            Id = "disable-failure-profile",
            Name = "Disable Failure Profile",
            Version = 1,
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "disable-failure" } }
        };
        var profileManager = new ThrowingDisableProfileManager(profile);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();

        var manager1 = CreateManager(profileManager, rollbackManager, logger, pauseSchedulingAfterUnsafeExit: false);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);

        rollbackManager.LastState = new SchedulerState();
        var manager2 = CreateManager(profileManager, rollbackManager, logger, pauseSchedulingAfterUnsafeExit: false);
        var firstRecovery = await manager2.BeginSessionAsync();
        Assert.Equal(1, firstRecovery.FailureCount);
        await manager2.TrackAppliedProfileAsync(profile);

        rollbackManager.LastState = new SchedulerState();
        var manager3 = CreateManager(profileManager, rollbackManager, logger, pauseSchedulingAfterUnsafeExit: false);
        var secondRecovery = await manager3.BeginSessionAsync();

        Assert.True(secondRecovery.ProfileDisableFailed);
        Assert.True(secondRecovery.SchedulingSuspended);
        Assert.False(secondRecovery.LastProfileDisabled);
        Assert.Equal(2, secondRecovery.FailureCount);

        var protectedSession = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json"));
        Assert.Contains("\"lastAppliedProfileId\": \"disable-failure-profile\"", protectedSession);
    }

    [Fact]
    public async Task BeginSessionAsync_DoesNotWriteThroughStartupStateDirectory()
    {
        var sessionPath = Path.Combine(_runtimeDirectory, "startup-session.json");
        Directory.CreateDirectory(sessionPath);
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var manager = CreateManager(profileManager, rollbackManager, logger);

        var result = await manager.BeginSessionAsync();

        Assert.True(result.StartupStateReadFailed);
        Assert.True(result.SchedulingSuspended);
        Assert.True(Directory.Exists(sessionPath));
    }

    [Fact]
    public async Task BeginSessionAsync_SuspendsSchedulingWhenFailureHistoryIsCorrupt()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager { LastState = new SchedulerState() };
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "corrupt-failure-history",
            Name = "Corrupt Failure History",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "corrupt-failure-history" } }
        };

        await profileManager.SaveProfileAsync(profile);
        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);
        await File.WriteAllTextAsync(Path.Combine(_runtimeDirectory, "startup-failures.json"), "{ not valid json");

        var manager2 = CreateManager(profileManager, rollbackManager, logger);
        var result = await manager2.BeginSessionAsync();

        Assert.True(result.FailureStateReadFailed);
        Assert.True(result.SchedulingSuspended);
        Assert.False(result.LastProfileDisabled);
        Assert.True((await profileManager.LoadProfilesAsync()).Single().IsEnabled);
        Assert.Equal(
            "{ not valid json",
            await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-failures.json")));

        await manager2.MarkCleanShutdownAsync();

        var protectedSession = await File.ReadAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-session.json"));
        Assert.Contains("\"sessionActive\": true", protectedSession, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkCleanShutdownAsync_KeepsSessionActiveWhenFailureHistoryIsCorrupt()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "clean-shutdown-corrupt-history",
            Name = "Clean Shutdown Corrupt History",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "clean-shutdown-corrupt-history" } }
        };

        await profileManager.SaveProfileAsync(profile);
        var manager = CreateManager(profileManager, rollbackManager, logger);
        await manager.BeginSessionAsync();
        await manager.TrackAppliedProfileAsync(profile);
        await File.WriteAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-failures.json"),
            "{ not valid json");

        await manager.MarkCleanShutdownAsync();

        var sessionJson = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json"));
        Assert.Contains("\"sessionActive\": true", sessionJson, StringComparison.OrdinalIgnoreCase);

        var nextManager = CreateManager(profileManager, rollbackManager, logger);
        var nextStartup = await nextManager.BeginSessionAsync();

        Assert.True(nextStartup.FailureStateReadFailed);
        Assert.True(nextStartup.SchedulingSuspended);
        Assert.Equal("{ not valid json", await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-failures.json")));
    }

    [Fact]
    public async Task BeginSessionAsync_TreatsCaseVariantFailureHistoryKeysAsAmbiguous()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        await File.WriteAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-session.json"),
            "{\"sessionActive\":true,\"lastAppliedProfileId\":\"Game\",\"lastAppliedProfileName\":\"Game\"}");
        await File.WriteAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-failures.json"),
            """
            {
              "profiles": {
                "Game": { "profileId": "Game", "consecutiveUnsafeRuns": 1 },
                "game": { "profileId": "game", "consecutiveUnsafeRuns": 2 }
              }
            }
            """);

        var manager = CreateManager(profileManager, rollbackManager, logger);
        var result = await manager.BeginSessionAsync();

        Assert.True(result.FailureStateReadFailed);
        Assert.True(result.SchedulingSuspended);
        Assert.Contains(
            logger.GetRecentEntries(),
            entry => entry.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BeginSessionAsync_SaturatesFailureCounterAtMaximum()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "overflow-profile",
            Name = "Overflow Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "overflow-profile" } }
        };

        await profileManager.SaveProfileAsync(profile);
        await File.WriteAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-session.json"),
            "{\"sessionActive\":true,\"lastAppliedProfileId\":\"overflow-profile\",\"lastAppliedProfileName\":\"Overflow Profile\"}");
        await File.WriteAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-failures.json"),
            "{\"profiles\":{\"overflow-profile\":{\"profileId\":\"overflow-profile\",\"consecutiveUnsafeRuns\":2147483647}}}");

        var manager = CreateManager(profileManager, rollbackManager, logger);
        var result = await manager.BeginSessionAsync();

        Assert.Equal(int.MaxValue, result.FailureCount);
        Assert.True(result.LastProfileDisabled);
        Assert.False((await profileManager.LoadProfilesAsync()).Single().IsEnabled);
    }

    [Fact]
    public async Task BeginSessionAsync_SuspendsSchedulingWhenFailureRecordIdDoesNotMatchItsKey()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var failurePath = Path.Combine(_runtimeDirectory, "startup-failures.json");
        var failureText = "{\"profiles\":{\"profile-key\":{\"profileId\":\"different-profile\",\"consecutiveUnsafeRuns\":1}}}";

        await File.WriteAllTextAsync(
            Path.Combine(_runtimeDirectory, "startup-session.json"),
            "{\"sessionActive\":true,\"lastAppliedProfileId\":\"profile-key\"}");
        await File.WriteAllTextAsync(failurePath, failureText);

        var manager = CreateManager(profileManager, rollbackManager, logger);
        var result = await manager.BeginSessionAsync();

        Assert.True(result.FailureStateReadFailed);
        Assert.True(result.SchedulingSuspended);
        Assert.Equal(failureText, await File.ReadAllTextAsync(failurePath));
    }

    [Fact]
    public async Task TrackAppliedProfileAsync_DoesNotOverwriteCorruptStartupState()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var manager = CreateManager(profileManager, rollbackManager, logger);
        var sessionPath = Path.Combine(_runtimeDirectory, "startup-session.json");
        await File.WriteAllTextAsync(sessionPath, "{ not valid json");

        await Assert.ThrowsAsync<StartupRecoveryStateUnavailableException>(() =>
            manager.TrackAppliedProfileAsync(new PerformanceProfile
            {
                Id = "untracked",
                Name = "Untracked",
                Match = new ProfileMatchCriteria { ProcessNames = new[] { "untracked" } }
            }));

        Assert.Equal("{ not valid json", await File.ReadAllTextAsync(sessionPath));
    }

    [Fact]
    public async Task BeginSessionAsync_KeepsSchedulingSuspendedWhenAutomaticRollbackFails()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager
        {
            LastState = new SchedulerState(),
            ThrowOnRollback = true
        };
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "rollback-failure-profile",
            Name = "Rollback Failure Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "rollback-failure" } }
        };

        await profileManager.SaveProfileAsync(profile);
        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);

        var manager2 = CreateManager(profileManager, rollbackManager, logger);
        var result = await manager2.BeginSessionAsync();

        Assert.True(result.PreviousSessionRecovered);
        Assert.True(result.SchedulingSuspended);
        Assert.False(result.RollbackApplied);
        Assert.True(result.RollbackFailed);
        Assert.Contains(
            logger.GetRecentEntries(),
            entry => entry.Message.Contains("could not apply automatic rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BeginSessionAsync_SuspendsSchedulingWhenRollbackLeavesStateEvenIfPauseWasDisabled()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager
        {
            LastState = new SchedulerState(),
            KeepStateAfterRollback = true
        };
        var logger = new TestLogger();
        var profile = new PerformanceProfile
        {
            Id = "unresolved-rollback-profile",
            Name = "Unresolved Rollback Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "unresolved-rollback" } }
        };

        await profileManager.SaveProfileAsync(profile);
        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();
        await manager1.TrackAppliedProfileAsync(profile);

        var manager2 = CreateManager(
            profileManager,
            rollbackManager,
            logger,
            pauseSchedulingAfterUnsafeExit: false);
        var result = await manager2.BeginSessionAsync();

        Assert.True(result.RollbackFailed);
        Assert.True(result.SchedulingSuspended);
        Assert.False(result.RollbackApplied);
    }

    [Fact]
    public async Task BeginSessionAsync_UnsafeShutdown_InvokesGpuRecoveryAction()
    {
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var gpuRecoveryCalled = false;

        var manager1 = CreateManager(profileManager, rollbackManager, logger);
        await manager1.BeginSessionAsync();

        var manager2 = new FileStartupRecoveryManager(
            Path.Combine(_runtimeDirectory, "startup-session.json"),
            Path.Combine(_runtimeDirectory, "startup-failures.json"),
            rollbackManager,
            profileManager,
            logger,
            autoRollbackOnUnsafeExit: true,
            disableProfileAfterRepeatedUnsafeRuns: true,
            pauseSchedulingAfterUnsafeExit: false,
            unsafeRunThreshold: 2,
            gpuRecoveryAction: ct =>
            {
                gpuRecoveryCalled = true;
                return Task.FromResult(true);
            });

        var result = await manager2.BeginSessionAsync();

        Assert.True(result.PreviousSessionRecovered);
        Assert.True(gpuRecoveryCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginSessionAsync_WhenGpuRecoveryFails_SuspendsEvenWithoutUnsafeMarker(bool unsafeMarker)
    {
        var profiles = new JsonProfileManager(_profilesDirectory);
        var rollback = new FakeRollbackManager();
        var logger = new TestLogger();
        if (unsafeMarker)
        {
            await CreateManager(profiles, rollback, logger).BeginSessionAsync();
        }

        var manager = CreateManager(profiles, rollback, logger,
            pauseSchedulingAfterUnsafeExit: false,
            gpuRecoveryAction: _ => Task.FromResult(false));

        var result = await manager.BeginSessionAsync();

        Assert.True(result.SchedulingSuspended);
        Assert.True(result.HasNotice);
    }

    [Fact]
    public async Task BeginSessionAsync_WhenGpuRecoveryThrows_SuspendsScheduling()
    {
        var profiles = new JsonProfileManager(_profilesDirectory);
        var rollback = new FakeRollbackManager();
        var logger = new TestLogger();
        await CreateManager(profiles, rollback, logger).BeginSessionAsync();
        var manager = CreateManager(profiles, rollback, logger,
            pauseSchedulingAfterUnsafeExit: false,
            gpuRecoveryAction: _ => throw new IOException("GPU reset unavailable"));

        var result = await manager.BeginSessionAsync();

        Assert.True(result.SchedulingSuspended);
    }

    [Fact]
    public async Task BeginSessionAsync_WhenGpuRecoveryIsCancelled_DoesNotContinueRecovery()
    {
        var profiles = new JsonProfileManager(_profilesDirectory);
        var rollback = new FakeRollbackManager();
        var logger = new TestLogger();
        await CreateManager(profiles, rollback, logger).BeginSessionAsync();
        var path = Path.Combine(_runtimeDirectory, "startup-session.json");
        var before = await File.ReadAllBytesAsync(path);
        rollback.LastState = new SchedulerState();
        using var cancellation = new CancellationTokenSource();
        var manager = CreateManager(profiles, rollback, logger,
            gpuRecoveryAction: token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<bool>(token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.BeginSessionAsync(cancellation.Token));

        Assert.Equal(0, rollback.RollbackCallCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MarkCleanShutdownAsync_WhenGpuRecoveryFailed_PreservesActiveSession()
    {
        var profiles = new JsonProfileManager(_profilesDirectory);
        var rollback = new FakeRollbackManager();
        var logger = new TestLogger();
        await CreateManager(profiles, rollback, logger).BeginSessionAsync();
        var manager = CreateManager(profiles, rollback, logger,
            pauseSchedulingAfterUnsafeExit: false,
            gpuRecoveryAction: _ => Task.FromResult(false));
        await manager.BeginSessionAsync();

        await manager.MarkCleanShutdownAsync();

        var json = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("sessionActive").GetBoolean());
    }

    [Fact]
    public async Task RecoverGpuAsync_WhenRetrySucceeds_AllowsCleanShutdown()
    {
        var profiles = new JsonProfileManager(_profilesDirectory);
        var rollback = new FakeRollbackManager();
        var logger = new TestLogger();
        var resetSucceeds = false;
        var manager = CreateManager(profiles, rollback, logger,
            gpuRecoveryAction: _ => Task.FromResult(resetSucceeds));
        var startup = await manager.BeginSessionAsync();
        Assert.True(startup.GpuRecoveryFailed);
        Assert.False(await manager.RecoverGpuAsync());

        resetSucceeds = true;
        Assert.True(await manager.RecoverGpuAsync());
        await manager.MarkCleanShutdownAsync();

        var json = await File.ReadAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("sessionActive").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private FileStartupRecoveryManager CreateManager(
        IProfileManager profileManager,
        FakeRollbackManager rollbackManager,
        TestLogger logger,
        bool pauseSchedulingAfterUnsafeExit = true,
        bool autoRollbackOnUnsafeExit = true,
        Func<CancellationToken, Task<bool>>? gpuRecoveryAction = null) =>
        new(
            Path.Combine(_runtimeDirectory, "startup-session.json"),
            Path.Combine(_runtimeDirectory, "startup-failures.json"),
            rollbackManager,
            profileManager,
            logger,
            autoRollbackOnUnsafeExit: autoRollbackOnUnsafeExit,
            disableProfileAfterRepeatedUnsafeRuns: true,
            pauseSchedulingAfterUnsafeExit: pauseSchedulingAfterUnsafeExit,
            unsafeRunThreshold: 2,
            gpuRecoveryAction: gpuRecoveryAction);

    private sealed class FakeRollbackManager : IRollbackManager
    {
        public SchedulerState? LastState { get; set; }

        public bool ThrowOnRollback { get; set; }

        public bool KeepStateAfterRollback { get; set; }

        public int RollbackCallCount { get; private set; }

        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
        {
            LastState = state;
            return Task.CompletedTask;
        }

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LastState);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCallCount++;
            if (ThrowOnRollback)
            {
                throw new IOException("rollback failed for test");
            }

            if (!KeepStateAfterRollback)
            {
                LastState = null;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDisableProfileManager(PerformanceProfile profile) : IProfileManager
    {
        public Task<IReadOnlyList<PerformanceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PerformanceProfile>>(new[] { profile });

        public Task SaveProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new IOException("profile store is unavailable for test");

        public Task<IReadOnlyList<ProfileRevisionInfo>> LoadProfileHistoryAsync(
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileRevisionInfo>>(Array.Empty<ProfileRevisionInfo>());

        public Task<PerformanceProfile?> RollbackProfileAsync(
            string profileId,
            string revisionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PerformanceProfile?>(null);

        public Task ExportProfilesAsync(string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ImportProfilesAsync(
            string sourcePath,
            bool overwriteExisting,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ConfigurationStoreSnapshot> CaptureImportSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationStoreSnapshot());

        public Task RestoreImportSnapshotAsync(
            ConfigurationStoreSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestLogger : IAppLogger
    {
        private readonly List<LogEntry> _entries = new();

        public event EventHandler<LogEntry>? EntryWritten;

        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) =>
            _entries.TakeLast(count).ToArray();

        public void Trace(string message) => Write(AppLogLevel.Trace, message);

        public void Info(string message) => Write(AppLogLevel.Information, message);

        public void Warn(string message) => Write(AppLogLevel.Warning, message);

        public void Error(string message, Exception? exception = null) =>
            Write(AppLogLevel.Error, exception is null ? message : $"{message} {exception.Message}");

        private void Write(AppLogLevel level, string message)
        {
            var entry = new LogEntry(DateTimeOffset.UtcNow, level, message);
            _entries.Add(entry);
            EntryWritten?.Invoke(this, entry);
        }
    }
}
