using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

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
    private readonly Func<CancellationToken, Task<bool>>? _gpuRecoveryAction;
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
        int unsafeRunThreshold = 2,
        Func<CancellationToken, Task<bool>>? gpuRecoveryAction = null)
    {
        _sessionStatePath = Path.GetFullPath(sessionStatePath);
        _failureStatePath = Path.GetFullPath(failureStatePath);
        _rollbackManager = rollbackManager;
        _profileManager = profileManager;
        _logger = logger;
        _autoRollbackOnUnsafeExit = autoRollbackOnUnsafeExit;
        _disableProfileAfterRepeatedUnsafeRuns = disableProfileAfterRepeatedUnsafeRuns;
        _pauseSchedulingAfterUnsafeExit = pauseSchedulingAfterUnsafeExit;
        _unsafeRunThreshold = Math.Max(1, unsafeRunThreshold);
        _gpuRecoveryAction = gpuRecoveryAction;

        EnsureParentDirectory(_sessionStatePath, "startup session");
        EnsureParentDirectory(_failureStatePath, "startup failure");
    }

    public async Task<StartupRecoveryResult> BeginSessionAsync(CancellationToken cancellationToken = default)
    {
        var loadedSession = await LoadStartupSessionAsync(cancellationToken);
        var previousSession = loadedSession.State ?? new StartupSessionState();
        var result = loadedSession.ReadFailed
            ? new StartupRecoveryResult
            {
                StartupStateReadFailed = true,
                SchedulingSuspended = true
            }
            : new StartupRecoveryResult();

        if (previousSession.SessionActive)
        {
            result = result with
            {
                PreviousSessionRecovered = true,
                SchedulingSuspended = _pauseSchedulingAfterUnsafeExit
            };
            _logger.Warn("Unsafe shutdown detected from the previous session.");
        }

        // GPU recovery has its own write-ahead marker and must be checked even
        // when the startup session marker is missing or describes a clean exit.
        if (!await RecoverGpuAsync(cancellationToken))
        {
            result = result with
            {
                GpuRecoveryFailed = true,
                SchedulingSuspended = true
            };
        }

        // A pending rollback is safety-critical on its own. Do not require a
        // readable startup marker before attempting to recover it: a crash can
        // happen after the rollback file is committed but before the marker is.
        try
        {
            var rollbackState = await _rollbackManager.GetLastKnownStateAsync(cancellationToken);
            if (rollbackState is not null && !_autoRollbackOnUnsafeExit)
            {
                result = result with
                {
                    RollbackPending = true,
                    SchedulingSuspended = true
                };
                _logger.Warn(
                    "Pending rollback state exists while automatic rollback is disabled; automatic scheduling will remain suspended.");
            }
            else if (_autoRollbackOnUnsafeExit && rollbackState is not null)
            {
                await _rollbackManager.RollbackAsync(cancellationToken);
                var remainingRollbackState = await _rollbackManager.GetLastKnownStateAsync(cancellationToken);
                if (remainingRollbackState is null)
                {
                    result = result with { RollbackApplied = true };
                    _logger.Warn("Startup recovery applied rollback automatically.");
                }
                else
                {
                    result = result with
                    {
                        RollbackFailed = true,
                        SchedulingSuspended = true
                    };
                    _logger.Error(
                        "Startup recovery left unresolved rollback state; automatic scheduling will remain suspended.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error(
                "Startup recovery could not apply automatic rollback; scheduling will remain suspended.",
                exception);
            result = result with
            {
                RollbackFailed = true,
                SchedulingSuspended = true
            };
        }

        if (previousSession.SessionActive)
        {
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

        if (result.ProfileDisableFailed)
        {
            // Keep the unsafe Profile associated with the protected session so a
            // later launch can retry the disable operation instead of losing the
            // safety decision when this suspended run exits.
            nextSession.LastAppliedAt = previousSession.LastAppliedAt;
            nextSession.LastAppliedProfileId = previousSession.LastAppliedProfileId;
            nextSession.LastAppliedProfileName = previousSession.LastAppliedProfileName;
        }

        if (loadedSession.ReadFailed || result.FailureStateReadFailed)
        {
            // Preserve unreadable startup metadata and failure history so a clean
            // shutdown cannot turn an unresolved safety decision into a clean run.
            _logger.Error(
                loadedSession.ReadFailed
                    ? "Startup session state remains protected; the new session marker was not written."
                    : "Startup failure history remains protected; the new session marker was not written.");
        }
        else
        {
            await SaveAsync(_sessionStatePath, nextSession, cancellationToken);
        }

        return result;
    }

    private async Task<(StartupSessionState? State, bool ReadFailed)> LoadStartupSessionAsync(
        CancellationToken cancellationToken)
    {
        if (!TryEnsureSafeStatePath(_sessionStatePath, "Startup session state"))
        {
            return (null, true);
        }

        if (!File.Exists(_sessionStatePath))
        {
            if (Directory.Exists(_sessionStatePath))
            {
                _logger.Error("Startup session state path is a directory and cannot be read safely.");
                return (null, true);
            }

            return (null, false);
        }

        try
        {
            // Re-check immediately before opening; startup metadata is
            // user-writable state and may change after the existence check.
            FileStoreSnapshot.EnsureSafeFilePath(_sessionStatePath);
            await using var stream = File.OpenRead(_sessionStatePath);
            var state = await JsonSerializer.DeserializeAsync<StartupSessionState>(
                stream,
                _jsonOptions,
                cancellationToken);
            return state is null ? (null, true) : (state, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Error(
                "Startup session state could not be read; automatic scheduling will remain suspended.",
                exception);
            return (null, true);
        }
    }

    public async Task TrackAppliedProfileAsync(PerformanceProfile? profile, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadStateAsync<StartupSessionState>(_sessionStatePath, cancellationToken);
        if (loaded.ReadFailed)
        {
            throw new StartupRecoveryStateUnavailableException(_sessionStatePath);
        }

        var state = loaded.State ?? new StartupSessionState();
        state.SessionActive = true;
        if (profile is null)
        {
            // A successful no-match evaluation means the previous profile has already
            // been restored. Do not carry its identity into the next crash report.
            state.LastAppliedAt = null;
            state.LastAppliedProfileId = null;
            state.LastAppliedProfileName = null;
        }
        else
        {
            state.LastAppliedAt = DateTimeOffset.UtcNow;
            state.LastAppliedProfileId = profile.Id;
            state.LastAppliedProfileName = profile.Name;
        }

        await SaveAsync(_sessionStatePath, state, cancellationToken);
    }

    public async Task<bool> RecoverGpuAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_gpuRecoveryAction is null)
        {
            return true;
        }

        try
        {
            if (await _gpuRecoveryAction(cancellationToken))
            {
                return true;
            }

            _logger.Error("GPU recovery remains incomplete; scheduling and clean-shutdown state remain protected.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("GPU recovery could not be completed; recovery state was preserved.", exception);
        }

        return false;
    }

    public async Task MarkCleanShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!await RecoverGpuAsync(cancellationToken))
        {
            return;
        }

        var loadedSession = await LoadStateAsync<StartupSessionState>(_sessionStatePath, cancellationToken);
        if (loadedSession.ReadFailed)
        {
            _logger.Error("Clean shutdown could not update unreadable startup session state.");
            return;
        }

        var session = loadedSession.State;
        if (session is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(session.LastAppliedProfileId))
        {
            var loadedFailureState = await LoadStateAsync<ProfileFailureState>(_failureStatePath, cancellationToken);
            if (loadedFailureState.ReadFailed)
            {
                _logger.Error("Clean shutdown could not update unreadable profile failure state.");
                // Do not mark the session clean when safety history cannot be
                // verified. The next launch must still enter startup protection.
                return;
            }

            var failureState = loadedFailureState.State ?? new ProfileFailureState();
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
        var loadedFailureState = await LoadStateAsync<ProfileFailureState>(_failureStatePath, cancellationToken);
        if (loadedFailureState.ReadFailed)
        {
            _logger.Error(
                "Profile failure state could not be read; the safety guard will not reset or overwrite its history.");
            return currentResult with
            {
                FailureStateReadFailed = true,
                SchedulingSuspended = true,
                LastProfileId = previousSession.LastAppliedProfileId,
                LastProfileName = previousSession.LastAppliedProfileName
            };
        }

        var failureState = loadedFailureState.State ?? new ProfileFailureState();
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
        record.ConsecutiveUnsafeRuns = record.ConsecutiveUnsafeRuns == int.MaxValue
            ? int.MaxValue
            : record.ConsecutiveUnsafeRuns + 1;
        record.LastFailureAt = DateTimeOffset.UtcNow;

        var result = currentResult with
        {
            LastProfileId = profileId,
            LastProfileName = record.ProfileName,
            FailureCount = record.ConsecutiveUnsafeRuns
        };

        if (record.ConsecutiveUnsafeRuns >= _unsafeRunThreshold)
        {
            PerformanceProfile? disabledProfile = null;
            try
            {
                disabledProfile = await DisableProfileAsync(profileId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error($"Profile {profileId} could not be disabled by the safety guard.", exception);
                result = result with
                {
                    ProfileDisableFailed = true,
                    SchedulingSuspended = true
                };
            }

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

    private async Task<StateLoadResult<T>> LoadStateAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!TryEnsureSafeStatePath(path, "Startup recovery state"))
        {
            return new StateLoadResult<T>(default, true);
        }

        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
            {
                _logger.Error($"Startup recovery state path '{path}' is a directory and cannot be read safely.");
                return new StateLoadResult<T>(default, true);
            }

            return new StateLoadResult<T>(default, false);
        }

        try
        {
            // Re-check immediately before opening; a state path can be
            // replaced between validation and the file handle acquisition.
            FileStoreSnapshot.EnsureSafeFilePath(path);
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
            if (state is ProfileFailureState failureState)
            {
                var normalizedFailureState = NormalizeFailureState(failureState);
                if (normalizedFailureState is null)
                {
                    return new StateLoadResult<T>(default, true);
                }

                state = (T)(object)normalizedFailureState;
            }

            return state is null || !IsValidLoadedState(state)
                ? new StateLoadResult<T>(default, true)
                : new StateLoadResult<T>(state, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warn($"Startup recovery state could not be read and will be ignored: {exception.Message}");
            return new StateLoadResult<T>(default, true);
        }
    }

    private async Task SaveAsync<T>(string path, T data, CancellationToken cancellationToken)
    {
        await AtomicFile.WriteJsonAsync(path, data, _jsonOptions, cancellationToken);
    }

    private void EnsureParentDirectory(string path, string stateName)
    {
        try
        {
            var parentDirectory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"The {stateName} state path has no parent directory.");
            FileStoreSnapshot.EnsureSafeDirectoryPath(parentDirectory);
            Directory.CreateDirectory(parentDirectory);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.Error($"The {stateName} state directory could not be prepared; safety metadata remains protected.", exception);
        }
    }

    private bool TryEnsureSafeStatePath(string path, string stateName)
    {
        try
        {
            FileStoreSnapshot.EnsureSafeFilePath(path);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.Error($"{stateName} path is unsafe; automatic scheduling will remain suspended.", exception);
            return false;
        }
    }

    private static bool IsValidLoadedState<T>(T state) =>
        state switch
        {
            ProfileFailureState failureState =>
                failureState.Profiles is not null &&
                failureState.Profiles.All(entry =>
                    !string.IsNullOrWhiteSpace(entry.Key) &&
                    entry.Value is not null &&
                    string.Equals(entry.Key, entry.Value.ProfileId, StringComparison.OrdinalIgnoreCase) &&
                    entry.Value.ConsecutiveUnsafeRuns >= 0),
            _ => true
        };

    private static ProfileFailureState? NormalizeFailureState(ProfileFailureState state)
    {
        if (state.Profiles is null)
        {
            return null;
        }

        var profiles = new Dictionary<string, ProfileFailureRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, record) in state.Profiles)
        {
            if (!profiles.TryAdd(profileId, record))
            {
                // Case-variant duplicate keys are ambiguous and must not be
                // silently merged into a different failure history.
                return null;
            }
        }

        return new ProfileFailureState { Profiles = profiles };
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

    private sealed record StateLoadResult<T>(T? State, bool ReadFailed);
}
