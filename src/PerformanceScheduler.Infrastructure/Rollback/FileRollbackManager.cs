using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Infrastructure.Rollback;

public sealed class FileRollbackManager : IRollbackManager
{
    private readonly string _stateFilePath;
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IPriorityManager _priorityManager;
    private readonly IAppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileRollbackManager(
        string stateFilePath,
        IPowerPlanManager powerPlanManager,
        IPriorityManager priorityManager,
        IAppLogger logger)
    {
        _stateFilePath = stateFilePath;
        _powerPlanManager = powerPlanManager;
        _priorityManager = priorityManager;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
    }

    public async Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
    {
        var existingState = await GetLastKnownStateAsync(cancellationToken);
        await SaveStateAsync(MergeState(existingState, state), cancellationToken);
    }

    public async Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            return await JsonSerializer.DeserializeAsync<SchedulerState>(stream, _jsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warn($"Rollback state could not be read and will be ignored: {exception.Message}");
            return null;
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetLastKnownStateAsync(cancellationToken);
        if (state is null)
        {
            _logger.Info("Rollback skipped because no previous state was captured.");
            return;
        }

        var remainingState = state;

        if (state.PreviousPowerPlan is not null)
        {
            if (state.AppliedPowerPlan is null)
            {
                remainingState = remainingState with
                {
                    PreviousPowerPlan = null,
                    AppliedPowerPlan = null
                };
                _logger.Warn("Skipped legacy power plan rollback because the applied plan was not captured.");
            }
            else
            {
                try
                {
                    var currentPlan = await _powerPlanManager.GetActivePlanAsync(cancellationToken);
                    if (currentPlan is null)
                    {
                        _logger.Warn("Failed to verify the current power plan; rollback state was preserved.");
                    }
                    else if (currentPlan.SchemeGuid != state.AppliedPowerPlan.SchemeGuid)
                    {
                        remainingState = remainingState with
                        {
                            PreviousPowerPlan = null,
                            AppliedPowerPlan = null
                        };
                        _logger.Info("Skipped power plan rollback because the active plan was changed after scheduling.");
                    }
                    else
                    {
                        var restored = await _powerPlanManager.SetActivePlanAsync(
                            state.PreviousPowerPlan.SchemeGuid,
                            cancellationToken);
                        if (restored)
                        {
                            remainingState = remainingState with
                            {
                                PreviousPowerPlan = null,
                                AppliedPowerPlan = null
                            };
                        }
                        else
                        {
                            _logger.Warn($"Failed to restore power plan {state.PreviousPowerPlan.Name}: powercfg did not accept the change.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore power plan {state.PreviousPowerPlan.Name}: {exception.Message}");
                }
            }
        }

        if (state.PreviousAdvancedPowerSettings is not null)
        {
            if (state.AppliedAdvancedPowerSettings is null)
            {
                remainingState = remainingState with
                {
                    PreviousAdvancedPowerSettings = null,
                    AppliedAdvancedPowerSettings = null
                };
                _logger.Warn("Skipped legacy advanced power rollback because the applied values were not captured.");
            }
            else
            {
                try
                {
                    var appliedState = state.AppliedAdvancedPowerSettings;
                    var currentState = await _powerPlanManager.GetAdvancedSettingsAsync(
                        appliedState.SchemeGuid,
                        appliedState.PowerSourceMode,
                        cancellationToken);
                    if (currentState is null)
                    {
                        _logger.Warn("Failed to verify current advanced power settings; rollback state was preserved.");
                    }
                    else if (currentState != appliedState)
                    {
                        remainingState = remainingState with
                        {
                            PreviousAdvancedPowerSettings = null,
                            AppliedAdvancedPowerSettings = null
                        };
                        _logger.Info("Skipped advanced power rollback because the settings were changed after scheduling.");
                    }
                    else
                    {
                        var restored = await _powerPlanManager.RestoreAdvancedSettingsAsync(
                            state.PreviousAdvancedPowerSettings,
                            cancellationToken);
                        if (restored)
                        {
                            remainingState = remainingState with
                            {
                                PreviousAdvancedPowerSettings = null,
                                AppliedAdvancedPowerSettings = null
                            };
                        }
                        else
                        {
                            _logger.Warn("Failed to restore advanced power settings: powercfg did not accept the change.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore advanced power settings: {exception.Message}");
                }
            }
        }

        var remainingPriorities = new Dictionary<int, PriorityLevel>(state.OriginalPriorities);
        var remainingAppliedPriorities = new Dictionary<int, PriorityLevel>(state.AppliedPriorities);
        var remainingIdentities = new Dictionary<int, ProcessIdentitySnapshot>(state.ProcessIdentities);
        var remainingEfficiencyModes = new Dictionary<int, ProcessEfficiencyModeState>(state.OriginalEfficiencyModes);
        var remainingAppliedEfficiencyModes = new Dictionary<int, bool>(state.AppliedEfficiencyModes);
        var processIds = state.OriginalPriorities.Keys
            .Concat(state.OriginalEfficiencyModes.Keys)
            .Distinct()
            .ToArray();

        foreach (var processId in processIds)
        {
            if (!state.ProcessIdentities.ContainsKey(processId))
            {
                RemoveProcessState(processId);
                _logger.Warn($"Skipped legacy process rollback for PID {processId} because no process identity was captured.");
                continue;
            }

            if (state.OriginalPriorities.TryGetValue(processId, out var priority))
            {
                if (await RestorePriorityAsync(processId, priority))
                {
                    RemoveProcessState(processId);
                    continue;
                }
            }

            if (state.OriginalEfficiencyModes.TryGetValue(processId, out var efficiencyMode))
            {
                if (await RestoreEfficiencyModeAsync(processId, efficiencyMode))
                {
                    RemoveProcessState(processId);
                    continue;
                }
            }

            if (!remainingPriorities.ContainsKey(processId) &&
                !remainingEfficiencyModes.ContainsKey(processId))
            {
                remainingIdentities.Remove(processId);
            }
        }

        remainingState = remainingState with
        {
            OriginalPriorities = remainingPriorities,
            AppliedPriorities = remainingAppliedPriorities,
            ProcessIdentities = remainingIdentities,
            OriginalEfficiencyModes = remainingEfficiencyModes,
            AppliedEfficiencyModes = remainingAppliedEfficiencyModes
        };
        if (IsEmpty(remainingState))
        {
            DeleteStateFile();
            return;
        }

        await SaveStateAsync(remainingState, cancellationToken);
        _logger.Warn("Rollback completed with unresolved item(s); remaining state was preserved.");

        async Task<bool> RestorePriorityAsync(int processId, PriorityLevel priority)
        {
            try
            {
                if (state.ProcessIdentities.TryGetValue(processId, out var identity) &&
                    state.AppliedPriorities.TryGetValue(processId, out var expectedPriority))
                {
                    var result = await _priorityManager.RestorePriorityIfUnchangedAsync(
                        identity,
                        expectedPriority,
                        priority,
                        cancellationToken);
                    switch (result)
                    {
                        case ConditionalProcessUpdateResult.Applied:
                            remainingPriorities.Remove(processId);
                            remainingAppliedPriorities.Remove(processId);
                            return false;
                        case ConditionalProcessUpdateResult.IdentityMismatch:
                            _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                            return true;
                        case ConditionalProcessUpdateResult.CurrentValueMismatch:
                            remainingPriorities.Remove(processId);
                            remainingAppliedPriorities.Remove(processId);
                            _logger.Info($"Skipped priority rollback for PID {processId} because its priority was changed after scheduling.");
                            return false;
                        default:
                            _logger.Warn($"Failed to restore priority for PID {processId}; rollback state was preserved.");
                            return false;
                    }
                }

                remainingPriorities.Remove(processId);
                remainingAppliedPriorities.Remove(processId);
                _logger.Warn($"Skipped priority rollback for PID {processId} because the applied priority was not captured.");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Warn($"Failed to restore priority for PID {processId}: {exception.Message}");
                return false;
            }
        }

        async Task<bool> RestoreEfficiencyModeAsync(int processId, ProcessEfficiencyModeState efficiencyMode)
        {
            try
            {
                if (state.ProcessIdentities.TryGetValue(processId, out var identity) &&
                    state.AppliedEfficiencyModes.TryGetValue(processId, out var expectedEfficiencyMode))
                {
                    var result = await _priorityManager.RestoreEfficiencyModeIfUnchangedAsync(
                        identity,
                        expectedEfficiencyMode,
                        efficiencyMode.IsEnabled,
                        cancellationToken);
                    switch (result)
                    {
                        case ConditionalProcessUpdateResult.Applied:
                            remainingEfficiencyModes.Remove(processId);
                            remainingAppliedEfficiencyModes.Remove(processId);
                            return false;
                        case ConditionalProcessUpdateResult.IdentityMismatch:
                            _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                            return true;
                        case ConditionalProcessUpdateResult.CurrentValueMismatch:
                            remainingEfficiencyModes.Remove(processId);
                            remainingAppliedEfficiencyModes.Remove(processId);
                            _logger.Info($"Skipped Efficiency Mode rollback for PID {processId} because it was changed after scheduling.");
                            return false;
                        default:
                            _logger.Warn($"Failed to restore Efficiency Mode for PID {processId}; rollback state was preserved.");
                            return false;
                    }
                }

                remainingEfficiencyModes.Remove(processId);
                remainingAppliedEfficiencyModes.Remove(processId);
                _logger.Warn($"Skipped Efficiency Mode rollback for PID {processId} because the applied state was not captured.");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Warn($"Failed to restore Efficiency Mode for PID {processId}: {exception.Message}");
                return false;
            }
        }

        void RemoveProcessState(int processId)
        {
            remainingPriorities.Remove(processId);
            remainingAppliedPriorities.Remove(processId);
            remainingIdentities.Remove(processId);
            remainingEfficiencyModes.Remove(processId);
            remainingAppliedEfficiencyModes.Remove(processId);
        }
    }

    private async Task SaveStateAsync(SchedulerState state, CancellationToken cancellationToken)
    {
        await AtomicFile.WriteJsonAsync(_stateFilePath, state, _jsonOptions, cancellationToken);
    }

    private void DeleteStateFile()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    private static bool IsEmpty(SchedulerState state) =>
        state.PreviousPowerPlan is null &&
        state.PreviousAdvancedPowerSettings is null &&
        state.OriginalPriorities.Count == 0 &&
        state.OriginalEfficiencyModes.Count == 0;

    private static SchedulerState MergeState(SchedulerState? existingState, SchedulerState newState)
    {
        if (existingState is null)
        {
            return newState;
        }

        var transactionId = MergeTransactionId(existingState.TransactionId, newState.TransactionId);

        var originalPriorities = new Dictionary<int, PriorityLevel>(existingState.OriginalPriorities);
        foreach (var (processId, priority) in newState.OriginalPriorities)
        {
            originalPriorities.TryAdd(processId, priority);
        }

        var appliedPriorities = MergeLatestDictionary(existingState.AppliedPriorities, newState.AppliedPriorities);
        var processIdentities = MergeDictionary(existingState.ProcessIdentities, newState.ProcessIdentities);
        var originalEfficiencyModes = MergeDictionary(
            existingState.OriginalEfficiencyModes,
            newState.OriginalEfficiencyModes);
        var appliedEfficiencyModes = MergeLatestDictionary(
            existingState.AppliedEfficiencyModes,
            newState.AppliedEfficiencyModes);

        return new SchedulerState
        {
            TransactionId = transactionId,
            CapturedAt = existingState.CapturedAt <= newState.CapturedAt
                ? existingState.CapturedAt
                : newState.CapturedAt,
            PreviousPowerPlan = existingState.PreviousPowerPlan ?? newState.PreviousPowerPlan,
            AppliedPowerPlan = newState.AppliedPowerPlan ?? existingState.AppliedPowerPlan,
            PreviousAdvancedPowerSettings = existingState.PreviousAdvancedPowerSettings ?? newState.PreviousAdvancedPowerSettings,
            AppliedAdvancedPowerSettings = newState.AppliedAdvancedPowerSettings ?? existingState.AppliedAdvancedPowerSettings,
            OriginalPriorities = originalPriorities,
            AppliedPriorities = appliedPriorities,
            ProcessIdentities = processIdentities,
            OriginalEfficiencyModes = originalEfficiencyModes,
            AppliedEfficiencyModes = appliedEfficiencyModes
        };
    }

    private static Guid MergeTransactionId(Guid existingTransactionId, Guid newTransactionId)
    {
        if (existingTransactionId == Guid.Empty)
        {
            return newTransactionId;
        }

        if (newTransactionId == Guid.Empty || existingTransactionId == newTransactionId)
        {
            return existingTransactionId;
        }

        throw new InvalidOperationException(
            $"Rollback state belongs to transaction {existingTransactionId}, not {newTransactionId}.");
    }

    private static Dictionary<TKey, TValue> MergeDictionary<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> existingValues,
        IReadOnlyDictionary<TKey, TValue> newValues)
        where TKey : notnull
    {
        var merged = new Dictionary<TKey, TValue>(existingValues);
        foreach (var (key, value) in newValues)
        {
            merged.TryAdd(key, value);
        }

        return merged;
    }

    private static Dictionary<TKey, TValue> MergeLatestDictionary<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> existingValues,
        IReadOnlyDictionary<TKey, TValue> newValues)
        where TKey : notnull
    {
        var merged = new Dictionary<TKey, TValue>(existingValues);
        foreach (var (key, value) in newValues)
        {
            merged[key] = value;
        }

        return merged;
    }
}
