using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Safety;

public sealed class FileStartupRecoveryManager : IStartupRecoveryManager
{
    private readonly string _sessionStatePath;
    private readonly string _failureStatePath;
    private readonly IRollbackManager _rollbackManager;
    private readonly IProfileManager _profileManager;
    private readonly IAppLogger _logger;
    private readonly int _unsafeRunThreshold;
    private readonly bool _autoRollbackOnUnsafeExit;
    private readonly bool _disableProfileAfterRepeatedUnsafeRuns;
    private readonly bool _pauseSchedulingAfterUnsafeExit;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileStartupRecoveryManager(
        string sessionStatePath,
        string failureStatePath,
        IRollbackManager rollbackManager,
        IProfileManager profileManager,
        IAppLogger logger,
        bool autoRollbackOnUnsafeExit,
        bool disableProfileAfterRepeatedUnsafeRuns,
        bool pauseSchedulingAfterUnsafeExit,
        int unsafeRunThreshold = 2)
    {
        _sessionStatePath = sessionStatePath;
        _failureStatePath = failureStatePath;
        _rollbackManager = rollbackManager;
        _profileManager = profileManager;
        _logger = logger;
        _autoRollbackOnUnsafeExit = autoRollbackOnUnsafeExit;
        _disableProfileAfterRepeatedUnsafeRuns = disableProfileAfterRepeatedUnsafeRuns;
        _pauseSchedulingAfterUnsafeExit = pauseSchedulingAfterUnsafeExit;
        _unsafeRunThreshold = Math.Max(1, unsafeRunThreshold);

        Directory.CreateDirectory(Path.GetDirectoryName(_sessionStatePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_failureStatePath)!);
    }

    public async Task<StartupRecoveryResult> BeginSessionAsync(CancellationToken cancellationToken = default)
    {
        var result = new StartupRecoveryResult();
        var previousSession = await LoadAsync<StartupSessionState>(_sessionStatePath, cancellationToken) ?? new StartupSessionState();

        if (previousSession.SessionActive)
        {
            result = result with
            {
                PreviousSessionRecovered = true,
                SchedulingSuspended = _pauseSchedulingAfterUnsafeExit
            };
            _logger.Warn("Unsafe shutdown detected from the previous session.");

            var rollbackState = await _rollbackManager.GetLastKnownStateAsync(cancellationToken);
            if (_autoRollbackOnUnsafeExit && rollbackState is not null)
            {
                await _rollbackManager.RollbackAsync(cancellationToken);
                result = result with { RollbackApplied = true };
                _logger.Warn("Startup recovery applied rollback automatically.");
            }

            if (_disableProfileAfterRepeatedUnsafeRuns &&
                !string.IsNullOrWhiteSpace(previousSession.LastAppliedProfileId))
            {
                result = await RegisterUnsafeRunAsync(previousSession, result, cancellationToken);
            }
        }

        var nextSession = new StartupSessionState
        {
            SessionActive = true,
            StartedAt = DateTimeOffset.UtcNow
        };

        await SaveAsync(_sessionStatePath, nextSession, cancellationToken);
        return result;
    }

    public async Task TrackAppliedProfileAsync(PerformanceProfile? profile, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return;
        }

        var state = await LoadAsync<StartupSessionState>(_sessionStatePath, cancellationToken) ?? new StartupSessionState();
        state.SessionActive = true;
        state.LastAppliedAt = DateTimeOffset.UtcNow;
        state.LastAppliedProfileId = profile.Id;
        state.LastAppliedProfileName = profile.Name;
        await SaveAsync(_sessionStatePath, state, cancellationToken);
    }

    public async Task MarkCleanShutdownAsync(CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync<StartupSessionState>(_sessionStatePath, cancellationToken);
        if (session is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(session.LastAppliedProfileId))
        {
            var failureState = await LoadAsync<ProfileFailureState>(_failureStatePath, cancellationToken) ?? new ProfileFailureState();
            if (failureState.Profiles.Remove(session.LastAppliedProfileId))
            {
                await SaveAsync(_failureStatePath, failureState, cancellationToken);
            }
        }

        session.SessionActive = false;
        session.LastCleanExitAt = DateTimeOffset.UtcNow;
        await SaveAsync(_sessionStatePath, session, cancellationToken);
    }

    private async Task<StartupRecoveryResult> RegisterUnsafeRunAsync(
        StartupSessionState previousSession,
        StartupRecoveryResult currentResult,
        CancellationToken cancellationToken)
    {
        var failureState = await LoadAsync<ProfileFailureState>(_failureStatePath, cancellationToken) ?? new ProfileFailureState();
        var profileId = previousSession.LastAppliedProfileId!;

        if (!failureState.Profiles.TryGetValue(profileId, out var record))
        {
            record = new ProfileFailureRecord
            {
                ProfileId = profileId
            };
            failureState.Profiles[profileId] = record;
        }

        record.ProfileName = previousSession.LastAppliedProfileName ?? record.ProfileName;
        record.ConsecutiveUnsafeRuns += 1;
        record.LastFailureAt = DateTimeOffset.UtcNow;

        var result = currentResult with
        {
            LastProfileId = profileId,
            LastProfileName = record.ProfileName,
            FailureCount = record.ConsecutiveUnsafeRuns
        };

        if (record.ConsecutiveUnsafeRuns >= _unsafeRunThreshold)
        {
            var disabledProfile = await DisableProfileAsync(profileId, cancellationToken);
            if (disabledProfile is not null)
            {
                record.DisabledBySafetyGuard = true;
                record.ProfileName = disabledProfile.Name;
                result = result with
                {
                    LastProfileDisabled = true,
                    LastProfileName = disabledProfile.Name
                };

                _logger.Warn(
                    $"Profile {disabledProfile.Name} was disabled after {record.ConsecutiveUnsafeRuns} unsafe session(s).");
            }
        }

        await SaveAsync(_failureStatePath, failureState, cancellationToken);
        return result;
    }

    private async Task<PerformanceProfile?> DisableProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var profiles = await _profileManager.LoadProfilesAsync(cancellationToken);
        var existing = profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (existing is null || !existing.IsEnabled)
        {
            return existing;
        }

        var disabledProfile = existing with
        {
            IsEnabled = false,
            Version = Math.Max(1, existing.Version + 1)
        };

        await _profileManager.SaveProfileAsync(disabledProfile, cancellationToken);
        return disabledProfile;
    }

    private async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warn($"Startup recovery state could not be read and will be ignored: {exception.Message}");
            return default;
        }
    }

    private async Task SaveAsync<T>(string path, T data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, _jsonOptions, cancellationToken);
    }

    private sealed class StartupSessionState
    {
        public bool SessionActive { get; set; }

        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastAppliedAt { get; set; }

        public string? LastAppliedProfileId { get; set; }

        public string? LastAppliedProfileName { get; set; }

        public DateTimeOffset? LastCleanExitAt { get; set; }
    }

    private sealed class ProfileFailureState
    {
        public Dictionary<string, ProfileFailureRecord> Profiles { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProfileFailureRecord
    {
        public string ProfileId { get; set; } = string.Empty;

        public string? ProfileName { get; set; }

        public int ConsecutiveUnsafeRuns { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }

        public bool DisabledBySafetyGuard { get; set; }
    }
}
