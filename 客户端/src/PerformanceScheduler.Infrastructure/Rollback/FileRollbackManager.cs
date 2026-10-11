using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Infrastructure.Rollback;

public sealed class FileRollbackManager : IRollbackManager
{
    private readonly string _stateFilePath;
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IPriorityManager _priorityManager;
    private readonly IAppLogger _logger;
    private readonly ICpuAffinityManager? _cpuAffinityManager;
    private readonly ICpuSetManager? _cpuSetManager;
    private readonly IProcessMemoryIoManager? _processMemoryIoManager;
    private readonly IProcessJobObjectManager? _processJobObjectManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileRollbackManager(
        string stateFilePath,
        IPowerPlanManager powerPlanManager,
        IPriorityManager priorityManager,
        IAppLogger logger,
        ICpuAffinityManager? cpuAffinityManager = null,
        ICpuSetManager? cpuSetManager = null,
        IProcessMemoryIoManager? processMemoryIoManager = null,
        IProcessJobObjectManager? processJobObjectManager = null)
    {
        _stateFilePath = stateFilePath;
        _powerPlanManager = powerPlanManager;
        _priorityManager = priorityManager;
        _logger = logger;
        _cpuAffinityManager = cpuAffinityManager;
        _cpuSetManager = cpuSetManager;
        _processMemoryIoManager = processMemoryIoManager;
        _processJobObjectManager = processJobObjectManager;
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
    }

    public async Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existingState = await GetLastKnownStateCoreAsync(cancellationToken);
            await SaveStateAsync(MergeState(existingState, state), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetLastKnownStateCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SchedulerState?> GetLastKnownStateCoreAsync(CancellationToken cancellationToken)
    {
        if (Directory.Exists(_stateFilePath))
        {
            throw new RollbackStateUnavailableException(
                _stateFilePath,
                new UnauthorizedAccessException($"Rollback state path '{_stateFilePath}' is a directory."));
        }

        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer.DeserializeAsync<SchedulerState>(stream, _jsonOptions, cancellationToken);
            if (state is null)
            {
                throw new JsonException("Rollback state was null.");
            }

            ValidateState(state);
            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RollbackStateUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warn($"Rollback state at '{_stateFilePath}' remains protected but could not be read: {exception.Message}");
            throw new RollbackStateUnavailableException(_stateFilePath, exception);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetLastKnownStateCoreAsync(cancellationToken);
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
                            var verifiedPlan = await _powerPlanManager.GetActivePlanAsync(cancellationToken);
                            if (restored && verifiedPlan?.SchemeGuid == state.PreviousPowerPlan.SchemeGuid)
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
                            var verifiedState = await _powerPlanManager.GetAdvancedSettingsAsync(
                                state.PreviousAdvancedPowerSettings.SchemeGuid,
                                state.PreviousAdvancedPowerSettings.PowerSourceMode,
                                cancellationToken);
                            if (restored && verifiedState == state.PreviousAdvancedPowerSettings)
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
            var remainingAffinities = new Dictionary<int, long>(state.OriginalCpuAffinities);
            var remainingAppliedAffinities = new Dictionary<int, long>(state.AppliedCpuAffinities);
            var remainingCpuSets = new Dictionary<int, IReadOnlyList<uint>>(state.OriginalCpuSets);
            var remainingAppliedCpuSets = new Dictionary<int, IReadOnlyList<uint>>(state.AppliedCpuSets);
            var remainingIoPriorities = new Dictionary<int, IoPriorityLevel>(state.OriginalIoPriorities);
            var remainingAppliedIoPriorities = new Dictionary<int, IoPriorityLevel>(state.AppliedIoPriorities);
            var remainingMemoryPriorities = new Dictionary<int, MemoryPriorityLevel>(state.OriginalMemoryPriorities);
            var remainingAppliedMemoryPriorities = new Dictionary<int, MemoryPriorityLevel>(state.AppliedMemoryPriorities);
            var remainingAppliedJobObjectLimits = new Dictionary<int, JobObjectLimitSnapshot>(state.AppliedJobObjectLimits);

            var processIds = state.OriginalPriorities.Keys
                .Concat(state.OriginalEfficiencyModes.Keys)
                .Concat(state.OriginalCpuAffinities.Keys)
                .Concat(state.OriginalCpuSets.Keys)
                .Concat(state.OriginalIoPriorities.Keys)
                .Concat(state.OriginalMemoryPriorities.Keys)
                .Concat(state.AppliedJobObjectLimits.Keys)
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

                if (state.OriginalCpuAffinities.TryGetValue(processId, out var affinity))
                {
                    if (await RestoreCpuAffinityAsync(processId, affinity))
                    {
                        RemoveProcessState(processId);
                        continue;
                    }
                }

                if (state.OriginalCpuSets.TryGetValue(processId, out var cpuSets))
                {
                    if (await RestoreCpuSetsAsync(processId, cpuSets))
                    {
                        RemoveProcessState(processId);
                        continue;
                    }
                }

                if (state.OriginalIoPriorities.TryGetValue(processId, out var ioPriority))
                {
                    if (await RestoreIoPriorityAsync(processId, ioPriority))
                    {
                        RemoveProcessState(processId);
                        continue;
                    }
                }

                if (state.OriginalMemoryPriorities.TryGetValue(processId, out var memoryPriority))
                {
                    if (await RestoreMemoryPriorityAsync(processId, memoryPriority))
                    {
                        RemoveProcessState(processId);
                        continue;
                    }
                }

                if (state.AppliedJobObjectLimits.TryGetValue(processId, out var jobLimits))
                {
                    if (await RestoreJobObjectLimitsAsync(processId, jobLimits))
                    {
                        RemoveProcessState(processId);
                        continue;
                    }
                }

                if (!remainingPriorities.ContainsKey(processId) &&
                    !remainingEfficiencyModes.ContainsKey(processId) &&
                    !remainingAffinities.ContainsKey(processId) &&
                    !remainingCpuSets.ContainsKey(processId) &&
                    !remainingIoPriorities.ContainsKey(processId) &&
                    !remainingMemoryPriorities.ContainsKey(processId) &&
                    !remainingAppliedJobObjectLimits.ContainsKey(processId))
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
                AppliedEfficiencyModes = remainingAppliedEfficiencyModes,
                OriginalCpuAffinities = remainingAffinities,
                AppliedCpuAffinities = remainingAppliedAffinities,
                OriginalCpuSets = remainingCpuSets,
                AppliedCpuSets = remainingAppliedCpuSets,
                OriginalIoPriorities = remainingIoPriorities,
                AppliedIoPriorities = remainingAppliedIoPriorities,
                OriginalMemoryPriorities = remainingMemoryPriorities,
                AppliedMemoryPriorities = remainingAppliedMemoryPriorities,
                AppliedJobObjectLimits = remainingAppliedJobObjectLimits
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

            async Task<bool> RestoreCpuAffinityAsync(int processId, long originalAffinity)
            {
                try
                {
                    if (_cpuAffinityManager is null)
                    {
                        remainingAffinities.Remove(processId);
                        remainingAppliedAffinities.Remove(processId);
                        _logger.Warn($"Skipped CPU affinity rollback for PID {processId} because no affinity manager was provided.");
                        return false;
                    }

                    if (state.ProcessIdentities.TryGetValue(processId, out var identity) &&
                        state.AppliedCpuAffinities.TryGetValue(processId, out var expectedAffinity))
                    {
                        var result = await _cpuAffinityManager.RestoreAffinityIfUnchangedAsync(
                            identity,
                            expectedAffinity,
                            originalAffinity,
                            cancellationToken);
                        switch (result)
                        {
                            case ConditionalProcessUpdateResult.Applied:
                                remainingAffinities.Remove(processId);
                                remainingAppliedAffinities.Remove(processId);
                                return false;
                            case ConditionalProcessUpdateResult.IdentityMismatch:
                                _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                                return true;
                            case ConditionalProcessUpdateResult.CurrentValueMismatch:
                                remainingAffinities.Remove(processId);
                                remainingAppliedAffinities.Remove(processId);
                                _logger.Info($"Skipped CPU affinity rollback for PID {processId} because its affinity was changed after scheduling.");
                                return false;
                            default:
                                _logger.Warn($"Failed to restore CPU affinity for PID {processId}; rollback state was preserved.");
                                return false;
                        }
                    }

                    remainingAffinities.Remove(processId);
                    remainingAppliedAffinities.Remove(processId);
                    _logger.Warn($"Skipped CPU affinity rollback for PID {processId} because the applied affinity was not captured.");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore CPU affinity for PID {processId}: {exception.Message}");
                    return false;
                }
            }

            async Task<bool> RestoreCpuSetsAsync(int processId, IReadOnlyList<uint> originalCpuSets)
            {
                try
                {
                    if (_cpuSetManager is null)
                    {
                        remainingCpuSets.Remove(processId);
                        remainingAppliedCpuSets.Remove(processId);
                        _logger.Warn($"Skipped CPU set rollback for PID {processId} because no CPU set manager was provided.");
                        return false;
                    }

                    if (state.ProcessIdentities.TryGetValue(processId, out var identity) &&
                        state.AppliedCpuSets.TryGetValue(processId, out var expectedCpuSets))
                    {
                        var result = await _cpuSetManager.RestoreCpuSetsIfUnchangedAsync(
                            identity,
                            expectedCpuSets,
                            originalCpuSets,
                            cancellationToken);
                        switch (result)
                        {
                            case ConditionalProcessUpdateResult.Applied:
                                remainingCpuSets.Remove(processId);
                                remainingAppliedCpuSets.Remove(processId);
                                return false;
                            case ConditionalProcessUpdateResult.IdentityMismatch:
                                _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                                return true;
                            case ConditionalProcessUpdateResult.CurrentValueMismatch:
                                remainingCpuSets.Remove(processId);
                                remainingAppliedCpuSets.Remove(processId);
                                _logger.Info($"Skipped CPU set rollback for PID {processId} because its CPU sets were changed after scheduling.");
                                return false;
                            default:
                                _logger.Warn($"Failed to restore CPU sets for PID {processId}; rollback state was preserved.");
                                return false;
                        }
                    }

                    remainingCpuSets.Remove(processId);
                    remainingAppliedCpuSets.Remove(processId);
                    _logger.Warn($"Skipped CPU set rollback for PID {processId} because the applied CPU sets were not captured.");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore CPU sets for PID {processId}: {exception.Message}");
                    return false;
                }
            }

            async Task<bool> RestoreIoPriorityAsync(int processId, IoPriorityLevel originalPriority)
            {
                try
                {
                    if (_processMemoryIoManager is null)
                    {
                        remainingIoPriorities.Remove(processId);
                        remainingAppliedIoPriorities.Remove(processId);
                        _logger.Warn($"Skipped I/O priority rollback for PID {processId} because no manager was provided.");
                        return false;
                    }

                    if (state.ProcessIdentities.TryGetValue(processId, out var identity) &&
                        state.AppliedIoPriorities.TryGetValue(processId, out var expectedPriority))
                    {
                        var result = await _processMemoryIoManager.RestoreIoPriorityIfUnchangedAsync(
                            identity,
                            expectedPriority,
                            originalPriority,
                            cancellationToken);
                        switch (result)
                        {
                            case ConditionalProcessUpdateResult.Applied:
                                remainingIoPriorities.Remove(processId);
                                remainingAppliedIoPriorities.Remove(processId);
                                return false;
                            case ConditionalProcessUpdateResult.IdentityMismatch:
                                _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                                return true;
                            case ConditionalProcessUpdateResult.CurrentValueMismatch:
                                remainingIoPriorities.Remove(processId);
                                remainingAppliedIoPriorities.Remove(processId);
                                _logger.Info($"Skipped I/O priority rollback for PID {processId} because its I/O priority was changed after scheduling.");
                                return false;
                            default:
                                _logger.Warn($"Failed to restore I/O priority for PID {processId}; rollback state was preserved.");
                                return false;
                        }
                    }

                    remainingIoPriorities.Remove(processId);
                    remainingAppliedIoPriorities.Remove(processId);
                    _logger.Warn($"Skipped I/O priority rollback for PID {processId} because the applied priority was not captured.");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore I/O priority for PID {processId}: {exception.Message}");
                    return false;
                }
            }

            async Task<bool> RestoreMemoryPriorityAsync(int processId, MemoryPriorityLevel originalPriority)
            {
                try
                {
                    if (_processMemoryIoManager is null)
                    {
                        remainingMemoryPriorities.Remove(processId);
                        remainingAppliedMemoryPriorities.Remove(processId);
                        _logger.Warn($"Skipped memory priority rollback for PID {processId} because no manager was provided.");
                        return false;
                    }

                    if (state.ProcessIdentities.TryGetValue(processId, out var identity) &&
                        state.AppliedMemoryPriorities.TryGetValue(processId, out var expectedPriority))
                    {
                        var result = await _processMemoryIoManager.RestoreMemoryPriorityIfUnchangedAsync(
                            identity,
                            expectedPriority,
                            originalPriority,
                            cancellationToken);
                        switch (result)
                        {
                            case ConditionalProcessUpdateResult.Applied:
                                remainingMemoryPriorities.Remove(processId);
                                remainingAppliedMemoryPriorities.Remove(processId);
                                return false;
                            case ConditionalProcessUpdateResult.IdentityMismatch:
                                _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                                return true;
                            case ConditionalProcessUpdateResult.CurrentValueMismatch:
                                remainingMemoryPriorities.Remove(processId);
                                remainingAppliedMemoryPriorities.Remove(processId);
                                _logger.Info($"Skipped memory priority rollback for PID {processId} because its memory priority was changed after scheduling.");
                                return false;
                            default:
                                _logger.Warn($"Failed to restore memory priority for PID {processId}; rollback state was preserved.");
                                return false;
                        }
                    }

                    remainingMemoryPriorities.Remove(processId);
                    remainingAppliedMemoryPriorities.Remove(processId);
                    _logger.Warn($"Skipped memory priority rollback for PID {processId} because the applied memory priority was not captured.");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore memory priority for PID {processId}: {exception.Message}");
                    return false;
                }
            }

            async Task<bool> RestoreJobObjectLimitsAsync(int processId, JobObjectLimitSnapshot expectedLimits)
            {
                try
                {
                    if (_processJobObjectManager is null)
                    {
                        remainingAppliedJobObjectLimits.Remove(processId);
                        _logger.Warn($"Skipped Job Object limits rollback for PID {processId} because no manager was provided.");
                        return false;
                    }

                    if (state.ProcessIdentities.TryGetValue(processId, out var identity))
                    {
                        var result = await _processJobObjectManager.RestoreProcessLimitsIfUnchangedAsync(
                            identity,
                            expectedLimits,
                            cancellationToken);
                        switch (result)
                        {
                            case ConditionalProcessUpdateResult.Applied:
                                remainingAppliedJobObjectLimits.Remove(processId);
                                return false;
                            case ConditionalProcessUpdateResult.IdentityMismatch:
                                _logger.Info($"Skipped rollback for PID {processId} because the original process has exited or its identity changed.");
                                return true;
                            case ConditionalProcessUpdateResult.CurrentValueMismatch:
                                remainingAppliedJobObjectLimits.Remove(processId);
                                _logger.Info($"Skipped Job Object limits rollback for PID {processId} because its limits were changed after scheduling.");
                                return false;
                            default:
                                _logger.Warn($"Failed to restore Job Object limits for PID {processId}; rollback state was preserved.");
                                return false;
                        }
                    }

                    remainingAppliedJobObjectLimits.Remove(processId);
                    _logger.Warn($"Skipped Job Object limits rollback for PID {processId} because no process identity was captured.");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"Failed to restore Job Object limits for PID {processId}: {exception.Message}");
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
                remainingAffinities.Remove(processId);
                remainingAppliedAffinities.Remove(processId);
                remainingCpuSets.Remove(processId);
                remainingAppliedCpuSets.Remove(processId);
                remainingIoPriorities.Remove(processId);
                remainingAppliedIoPriorities.Remove(processId);
                remainingMemoryPriorities.Remove(processId);
                remainingAppliedMemoryPriorities.Remove(processId);
                remainingAppliedJobObjectLimits.Remove(processId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateState(SchedulerState state)
    {
        if (state.OriginalPriorities is null ||
            state.AppliedPriorities is null ||
            state.ProcessIdentities is null ||
            state.OriginalEfficiencyModes is null ||
            state.AppliedEfficiencyModes is null ||
            state.OriginalCpuAffinities is null ||
            state.AppliedCpuAffinities is null ||
            state.OriginalCpuSets is null ||
            state.AppliedCpuSets is null ||
            state.OriginalIoPriorities is null ||
            state.AppliedIoPriorities is null ||
            state.OriginalMemoryPriorities is null ||
            state.AppliedMemoryPriorities is null ||
            state.AppliedJobObjectLimits is null)
        {
            throw new InvalidOperationException("Rollback state collections must not be null.");
        }

        foreach (var processId in state.AppliedPriorities.Keys)
        {
            if (!state.OriginalPriorities.ContainsKey(processId))
            {
                throw new InvalidOperationException($"Applied priority for PID {processId} has no corresponding original priority.");
            }
        }

        foreach (var processId in state.AppliedEfficiencyModes.Keys)
        {
            if (!state.OriginalEfficiencyModes.ContainsKey(processId))
            {
                throw new InvalidOperationException($"Applied efficiency mode for PID {processId} has no corresponding original efficiency mode.");
            }
        }

        foreach (var processId in state.AppliedCpuAffinities.Keys)
        {
            if (!state.OriginalCpuAffinities.ContainsKey(processId))
            {
                throw new InvalidOperationException($"Applied CPU affinity for PID {processId} has no corresponding original CPU affinity.");
            }
        }

        foreach (var processId in state.AppliedCpuSets.Keys)
        {
            if (!state.OriginalCpuSets.ContainsKey(processId))
            {
                throw new InvalidOperationException($"Applied CPU set for PID {processId} has no corresponding original CPU set.");
            }
        }

        foreach (var processId in state.AppliedIoPriorities.Keys)
        {
            if (!state.OriginalIoPriorities.ContainsKey(processId))
            {
                throw new InvalidOperationException($"Applied I/O priority for PID {processId} has no corresponding original priority.");
            }
        }

        foreach (var processId in state.AppliedMemoryPriorities.Keys)
        {
            if (!state.OriginalMemoryPriorities.ContainsKey(processId))
            {
                throw new InvalidOperationException($"Applied memory priority for PID {processId} has no corresponding original priority.");
            }
        }

        foreach (var (processId, identity) in state.ProcessIdentities)
        {
            if (identity is null || identity.ProcessId != processId)
            {
                throw new InvalidOperationException($"Process identity mismatch for PID {processId}.");
            }
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
        state.OriginalEfficiencyModes.Count == 0 &&
        state.OriginalCpuAffinities.Count == 0 &&
        state.OriginalCpuSets.Count == 0 &&
        state.OriginalIoPriorities.Count == 0 &&
        state.OriginalMemoryPriorities.Count == 0 &&
        state.AppliedJobObjectLimits.Count == 0;

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

        var originalAffinities = new Dictionary<int, long>(existingState.OriginalCpuAffinities);
        foreach (var (processId, affinity) in newState.OriginalCpuAffinities)
        {
            originalAffinities.TryAdd(processId, affinity);
        }
        var appliedAffinities = MergeLatestDictionary(existingState.AppliedCpuAffinities, newState.AppliedCpuAffinities);

        var originalCpuSets = new Dictionary<int, IReadOnlyList<uint>>(existingState.OriginalCpuSets);
        foreach (var (processId, cpuSets) in newState.OriginalCpuSets)
        {
            originalCpuSets.TryAdd(processId, cpuSets);
        }
        var appliedCpuSets = MergeLatestDictionary(existingState.AppliedCpuSets, newState.AppliedCpuSets);

        var originalIoPriorities = new Dictionary<int, IoPriorityLevel>(existingState.OriginalIoPriorities);
        foreach (var (processId, priority) in newState.OriginalIoPriorities)
        {
            originalIoPriorities.TryAdd(processId, priority);
        }
        var appliedIoPriorities = MergeLatestDictionary(existingState.AppliedIoPriorities, newState.AppliedIoPriorities);

        var originalMemoryPriorities = new Dictionary<int, MemoryPriorityLevel>(existingState.OriginalMemoryPriorities);
        foreach (var (processId, priority) in newState.OriginalMemoryPriorities)
        {
            originalMemoryPriorities.TryAdd(processId, priority);
        }
        var appliedMemoryPriorities = MergeLatestDictionary(existingState.AppliedMemoryPriorities, newState.AppliedMemoryPriorities);
        var appliedJobObjectLimits = MergeLatestDictionary(existingState.AppliedJobObjectLimits, newState.AppliedJobObjectLimits);

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
            AppliedEfficiencyModes = appliedEfficiencyModes,
            OriginalCpuAffinities = originalAffinities,
            AppliedCpuAffinities = appliedAffinities,
            OriginalCpuSets = originalCpuSets,
            AppliedCpuSets = appliedCpuSets,
            OriginalIoPriorities = originalIoPriorities,
            AppliedIoPriorities = appliedIoPriorities,
            OriginalMemoryPriorities = originalMemoryPriorities,
            AppliedMemoryPriorities = appliedMemoryPriorities,
            AppliedJobObjectLimits = appliedJobObjectLimits
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
