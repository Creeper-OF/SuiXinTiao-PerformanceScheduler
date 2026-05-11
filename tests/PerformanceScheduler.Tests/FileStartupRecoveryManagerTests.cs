using PerformanceScheduler.Core.Abstractions;
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
            Version = 1
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
            Name = "Stable Profile"
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
    public async Task BeginSessionAsync_IgnoresCorruptStartupState()
    {
        await File.WriteAllTextAsync(Path.Combine(_runtimeDirectory, "startup-session.json"), "{ not valid json");
        var profileManager = new JsonProfileManager(_profilesDirectory);
        var rollbackManager = new FakeRollbackManager();
        var logger = new TestLogger();
        var manager = CreateManager(profileManager, rollbackManager, logger);

        var result = await manager.BeginSessionAsync();

        Assert.False(result.PreviousSessionRecovered);
        Assert.Contains(
            logger.GetRecentEntries(),
            entry => entry.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private FileStartupRecoveryManager CreateManager(
        JsonProfileManager profileManager,
        FakeRollbackManager rollbackManager,
        TestLogger logger) =>
        new(
            Path.Combine(_runtimeDirectory, "startup-session.json"),
            Path.Combine(_runtimeDirectory, "startup-failures.json"),
            rollbackManager,
            profileManager,
            logger,
            autoRollbackOnUnsafeExit: true,
            disableProfileAfterRepeatedUnsafeRuns: true,
            pauseSchedulingAfterUnsafeExit: true,
            unsafeRunThreshold: 2);

    private sealed class FakeRollbackManager : IRollbackManager
    {
        public SchedulerState? LastState { get; set; }

        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
        {
            LastState = state;
            return Task.CompletedTask;
        }

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LastState);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            LastState = null;
            return Task.CompletedTask;
        }
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
