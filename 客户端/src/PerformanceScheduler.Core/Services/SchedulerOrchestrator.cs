using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class SchedulerOrchestrator
{
    private readonly IForegroundWatcher _foregroundWatcher;
    private readonly IProcessInspector _processInspector;
    private readonly ICapabilityDetector _capabilityDetector;
    private readonly IPowerStatusProvider _powerStatusProvider;
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IProfileManager _profileManager;
    private readonly IRollbackManager _rollbackManager;
    private readonly IMetricsCollector _metricsCollector;
    private readonly IRuntimeStateStore _runtimeStateStore;
    private readonly IAppLogger _logger;
    private readonly ISchedulerActionExecutor _actionExecutor;
    private readonly ProfileMatcher _profileMatcher;
    private readonly IRuntimeProfileResolver? _runtimeProfileResolver;
    private readonly IResourceArbitrationProvider? _resourceArbitrationProvider;
    private readonly ITargetExperienceAdaptiveCoordinator? _targetExperienceAdaptiveCoordinator;
    private readonly SchedulingSessionCoordinator _sessionCoordinator;
    private readonly Func<DateTimeOffset> _utcNow;
    private string? _lastRuntimeProfileSelectionLogKey;

    public SchedulerOrchestrator(
        IForegroundWatcher foregroundWatcher,
        IProcessInspector processInspector,
        ICapabilityDetector capabilityDetector,
        IPowerStatusProvider powerStatusProvider,
        IPowerPlanManager powerPlanManager,
        IPriorityManager priorityManager,
        IProfileManager profileManager,
        IRollbackManager rollbackManager,
        IMetricsCollector metricsCollector,
        IRuntimeStateStore runtimeStateStore,
        IAppLogger logger,
        ProfileMatcher? profileMatcher = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? failureRetryBaseDelay = null,
        TimeSpan? failureRetryMaxDelay = null,
        ISchedulerActionExecutor? actionExecutor = null,
        IRuntimeProfileResolver? runtimeProfileResolver = null,
        IResourceArbitrationProvider? resourceArbitrationProvider = null,
        ITargetExperienceAdaptiveCoordinator? targetExperienceAdaptiveCoordinator = null,
        ICpuAffinityManager? cpuAffinityManager = null,
        ICpuSetManager? cpuSetManager = null,
        IProcessMemoryIoManager? processMemoryIoManager = null,
        IProcessJobObjectManager? processJobObjectManager = null)
    {
        _foregroundWatcher = foregroundWatcher;
        _processInspector = processInspector;
        _capabilityDetector = capabilityDetector;
        _powerStatusProvider = powerStatusProvider;
        _powerPlanManager = powerPlanManager;
        _profileManager = profileManager;
        _rollbackManager = rollbackManager;
        _metricsCollector = metricsCollector;
        _runtimeStateStore = runtimeStateStore;
        _logger = logger;
        _actionExecutor = actionExecutor ?? new SchedulerActionExecutor(
            powerPlanManager,
            priorityManager,
            rollbackManager,
            logger,
            cpuAffinityManager,
            cpuSetManager,
            processMemoryIoManager,
            processJobObjectManager);
        _profileMatcher = profileMatcher ?? new ProfileMatcher();
        _runtimeProfileResolver = runtimeProfileResolver;
        _resourceArbitrationProvider = resourceArbitrationProvider;
        _targetExperienceAdaptiveCoordinator = targetExperienceAdaptiveCoordinator;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _sessionCoordinator = new SchedulingSessionCoordinator(
            failureRetryBaseDelay,
            failureRetryMaxDelay);
    }

    public Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _capabilityDetector.DetectAsync(cancellationToken);

    public void InvalidateCapabilities()
    {
        if (_capabilityDetector is ICapabilityCacheInvalidator invalidator)
        {
            invalidator.InvalidateCache();
        }
    }

    public async Task<SchedulerRunResult> EvaluateForegroundAsync(CancellationToken cancellationToken = default)
        => await _sessionCoordinator.RunExclusiveAsync(
            () => EvaluateForegroundCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<SchedulerRunResult> EvaluateForegroundCoreAsync(CancellationToken cancellationToken)
    {
        var capabilities = await _capabilityDetector.DetectAsync(cancellationToken);
        var foregroundWindow = await _foregroundWatcher.GetForegroundWindowAsync(cancellationToken);
        if (foregroundWindow is null)
        {
            const string message = "Unable to determine the foreground window.";
            _logger.Warn(message);
            return await PersistForegroundUnavailableResultAsync(capabilities, message, cancellationToken);
        }

        var app = await _processInspector.InspectAsync(foregroundWindow, cancellationToken);
        if (app is null)
        {
            var message = $"Unable to inspect process {foregroundWindow.ProcessId}.";
            _logger.Warn(message);
            return await PersistForegroundUnavailableResultAsync(capabilities, message, cancellationToken);
        }

        var powerSourceMode = await _powerStatusProvider.GetCurrentPowerSourceAsync(cancellationToken);
        _logger.Info($"Foreground app detected: {app.ProcessName} ({app.ProcessId})");
        app = app with { PowerSourceMode = powerSourceMode };

        var localProfiles = await _profileManager.LoadProfilesAsync(cancellationToken);
        var profileSelection = await ResolveRuntimeProfilesAsync(
            localProfiles,
            powerSourceMode,
            cancellationToken);
        LogRuntimeProfileSelection(profileSelection);
        Task<SchedulerRunResult> PersistWithSelectionAsync(SchedulerRunResult result) =>
            PersistResultAsync(
                result with { DeviceStrategySelection = profileSelection },
                cancellationToken);

        var match = _profileMatcher.Match(app, profileSelection.Profiles);
        var requestedSession = match is null
            ? null
            : SchedulingSessionIdentity.Create(app, match.Profile, powerSourceMode);

        if (_sessionCoordinator.RequiresTransition(requestedSession))
        {
            var restored = await RestoreActiveSessionAsync(cancellationToken);
            if (!restored)
            {
                return await PersistWithSelectionAsync(new SchedulerRunResult
                {
                    ActiveApp = app,
                    MatchResult = match,
                    Capabilities = capabilities,
                    PowerSourceMode = powerSourceMode,
                    Success = false,
                    FailureRecoveryCompleted = false,
                    Summary = "The previous scheduling session could not be fully restored. New adjustments were blocked."
                });
            }
        }

        SchedulerState? pendingRollbackState = null;
        if (_sessionCoordinator.ActiveSession is null)
        {
            try
            {
                pendingRollbackState = await _rollbackManager.GetLastKnownStateAsync(cancellationToken);
            }
            catch (RollbackStateUnavailableException exception)
            {
                const string message = "Rollback state could not be read. New adjustments were blocked until the state is repaired or removed.";
                _logger.PublishUserEvent(
                    UserEventCode.RollbackStateUnreadable,
                    AppLogLevel.Error,
                    $"{message} Path: {exception.StateFilePath}");
                return await PersistWithSelectionAsync(new SchedulerRunResult
                {
                    ActiveApp = app,
                    MatchResult = match,
                    Capabilities = capabilities,
                    PowerSourceMode = powerSourceMode,
                    Success = false,
                    FailureRecoveryCompleted = false,
                    Summary = message
                });
            }
        }

        if (_sessionCoordinator.ActiveSession is null &&
            pendingRollbackState is not null)
        {
            const string message = "Pending rollback state exists. New adjustments were blocked until recovery succeeds.";
            _logger.PublishUserEvent(
                UserEventCode.PendingRollbackBlocked,
                AppLogLevel.Warning,
                message);
            return await PersistWithSelectionAsync(new SchedulerRunResult
            {
                ActiveApp = app,
                MatchResult = match,
                Capabilities = capabilities,
                PowerSourceMode = powerSourceMode,
                Success = false,
                FailureRecoveryCompleted = false,
                Summary = message
            });
        }

        PowerPlanInfo? activePowerPlan = capabilities.SupportsPowerPlanSwitching
            ? await _powerPlanManager.GetActivePlanAsync(cancellationToken)
            : capabilities.AvailablePowerPlans.FirstOrDefault(plan => plan.IsActive);

        if (requestedSession is not null &&
            _sessionCoordinator.GetActiveFailureCooldown(requestedSession, _utcNow()) is { } failureCooldown)
        {
            var message = $"Profile {match!.Profile.Name} is waiting until " +
                          $"{failureCooldown.RetryAfter.LocalDateTime:G} before retrying after " +
                          $"{failureCooldown.ConsecutiveFailureCount} consecutive failure(s).";
            _logger.PublishUserEvent(
                UserEventCode.ProfileRetryDeferred,
                AppLogLevel.Warning,
                message,
                match.Profile.Name,
                failureCooldown.RetryAfter.LocalDateTime,
                failureCooldown.ConsecutiveFailureCount);
            return await PersistWithSelectionAsync(new SchedulerRunResult
            {
                ActiveApp = app,
                MatchResult = match,
                ActivePowerPlan = activePowerPlan,
                Capabilities = capabilities,
                PowerSourceMode = powerSourceMode,
                Success = false,
                IsRetryDeferred = true,
                ConsecutiveFailureCount = failureCooldown.ConsecutiveFailureCount,
                RetryAfter = failureCooldown.RetryAfter,
                FailureRecoveryCompleted = true,
                Summary = message
            });
        }

        if (requestedSession is not null && _sessionCoordinator.IsCurrent(requestedSession))
        {
            MetricsSnapshot? currentMetrics;
            try
            {
                currentMetrics = capabilities.SupportsMetricsCollection
                    ? await _metricsCollector.CollectAsync(app, cancellationToken)
                    : null;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Metrics are diagnostic-only. Keep an already active session usable
                // when a sampling provider fails on a later foreground scan.
                _logger.Error("Metrics collection failed for the active scheduling session.", exception);
                currentMetrics = null;
            }

            return await PersistWithSelectionAsync(new SchedulerRunResult
            {
                TransactionId = _sessionCoordinator.ActiveTransactionId,
                ActiveApp = app,
                MatchResult = match,
                ActivePowerPlan = activePowerPlan,
                Metrics = currentMetrics,
                Capabilities = capabilities,
                PowerSourceMode = powerSourceMode,
                Success = true,
                Summary = $"Profile {match!.Profile.Name} remains active for {app.ProcessName}."
            });
        }

        var transactionId = requestedSession is null ? (Guid?)null : Guid.NewGuid();
        PriorityAdjustmentResult? priorityResult = null;
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments =
            Array.Empty<BackgroundProcessAdjustmentResult>();
        IReadOnlyList<SchedulerActionResult> actionResults = Array.Empty<SchedulerActionResult>();

        ResourceArbitrationSummary? arbitrationSummary = null;
        if (match is null)
        {
            _logger.Info($"No profile matched for {app.ProcessName}.");
        }
        else
        {
            if (match.Profile.Priority.UseResourceArbitration && _resourceArbitrationProvider is not null)
            {
                try
                {
                    arbitrationSummary = await _resourceArbitrationProvider.GetArbitrationSummaryAsync(
                        app,
                        match.Profile,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Error(
                        $"Resource arbitration evaluation failed for {app.ProcessName}; falling back to conservative missing-evidence gating.",
                        exception);
                    arbitrationSummary = null;
                }
            }

            SchedulerActionExecutionResult execution;
            try
            {
                execution = await _actionExecutor.ExecuteAsync(new SchedulerActionExecutionRequest
                {
                    TransactionId = transactionId!.Value,
                    ActiveApp = app,
                    Match = match,
                    Capabilities = capabilities,
                    PowerSourceMode = powerSourceMode,
                    ActivePowerPlan = activePowerPlan,
                    UseResourceArbitration = match.Profile.Priority.UseResourceArbitration,
                    ResourceArbitration = arbitrationSummary
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A cancellation can arrive after a provider has already changed state.
                // Use an independent token so cancellation cannot skip the safety rollback.
                await RestoreFailedRunAsync(transactionId!.Value, CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error(
                    $"Scheduling action execution failed for profile {match.Profile.Name}.",
                    exception);
                var recoveryCompleted = await RestoreFailedRunAsync(
                    transactionId!.Value,
                    CancellationToken.None);
                var actionFailureState = _sessionCoordinator.MarkFailed(requestedSession!, _utcNow());
                var recoverySummary = recoveryCompleted
                    ? "Automatic rollback completed."
                    : "Automatic rollback is incomplete; new adjustments remain blocked.";
                return await PersistWithSelectionAsync(new SchedulerRunResult
                {
                    TransactionId = transactionId,
                    ActiveApp = app,
                    MatchResult = match,
                    ActivePowerPlan = activePowerPlan,
                    Capabilities = capabilities,
                    PowerSourceMode = powerSourceMode,
                    Success = false,
                    ConsecutiveFailureCount = actionFailureState.ConsecutiveFailureCount,
                    RetryAfter = actionFailureState.RetryAfter,
                    FailureRecoveryCompleted = recoveryCompleted,
                    Summary = $"Scheduling action execution failed. {recoverySummary}"
                });
            }

            activePowerPlan = execution.ActivePowerPlan;
            priorityResult = execution.PriorityChange;
            backgroundAdjustments = execution.BackgroundAdjustments;
            actionResults = execution.Actions;
        }

        var failedActions = actionResults
            .Where(action => action.Status == SchedulerActionStatus.Failed)
            .ToArray();
        var runSucceeded = failedActions.Length == 0;
        var failureRecoveryCompleted = true;
        SchedulingFailureState? failureState = null;
        if (match is not null && !runSucceeded)
        {
            failureRecoveryCompleted = await RestoreFailedRunAsync(
                transactionId!.Value,
                CancellationToken.None);
            failureState = _sessionCoordinator.MarkFailed(requestedSession!, _utcNow());
            cancellationToken.ThrowIfCancellationRequested();
        }
        MetricsSnapshot? metrics;
        try
        {
            metrics = capabilities.SupportsMetricsCollection
                ? await _metricsCollector.CollectAsync(app, cancellationToken)
                : null;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transactionId is { } cancelledTransactionId)
            {
                await RestoreFailedRunAsync(cancelledTransactionId, CancellationToken.None);
            }

            throw;
        }
        catch (Exception exception)
        {
            // Metrics are diagnostic-only. A provider failure must not turn an
            // already safe scheduling action into an untracked partial session.
            _logger.Error("Metrics collection failed after scheduling action execution.", exception);
            metrics = null;
        }

        var summary = match is null
            ? $"Foreground app {app.ProcessName} has no matching profile."
            : runSucceeded
                ? SchedulerResultSummaryBuilder.Build(app, match, backgroundAdjustments, capabilities, priorityResult)
                : SchedulerResultSummaryBuilder.BuildFailure(
                    app,
                    match,
                    failedActions,
                    failureRecoveryCompleted);

        var result = new SchedulerRunResult
        {
            TransactionId = transactionId,
            ActiveApp = app,
            MatchResult = match,
            ActivePowerPlan = activePowerPlan,
            PriorityChange = priorityResult,
            BackgroundAdjustments = backgroundAdjustments,
            Actions = actionResults,
            Metrics = metrics,
            Capabilities = capabilities,
            PowerSourceMode = powerSourceMode,
            ResourceArbitration = arbitrationSummary,
            Success = runSucceeded,
            ConsecutiveFailureCount = failureState?.ConsecutiveFailureCount ?? 0,
            RetryAfter = failureState?.RetryAfter,
            FailureRecoveryCompleted = runSucceeded ? null : failureRecoveryCompleted,
            Summary = summary
        };

        if (match is not null && runSucceeded && capabilities.SupportsRollback)
        {
            return await PersistNewSessionResultAsync(
                result with { DeviceStrategySelection = profileSelection },
                requestedSession!,
                transactionId!.Value,
                cancellationToken);
        }

        return await PersistWithSelectionAsync(result);
    }

    private async Task<DeviceStrategyRuntimeSelection> ResolveRuntimeProfilesAsync(
        IReadOnlyList<PerformanceProfile> localProfiles,
        PowerSourceMode powerSourceMode,
        CancellationToken cancellationToken)
    {
        if (_runtimeProfileResolver is null)
        {
            return DeviceStrategyRuntimeSelection.LocalOnly(localProfiles, powerSourceMode);
        }

        try
        {
            return await _runtimeProfileResolver.ResolveAsync(
                localProfiles,
                powerSourceMode,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A package or fingerprint failure must never disable the local
            // Profile library or block a safe scheduling evaluation.
            _logger.Error(
                "Device strategy resolution failed; falling back to local Profiles.",
                exception);
            return DeviceStrategyRuntimeSelection.LocalOnly(
                localProfiles,
                powerSourceMode,
                "DeviceStrategyRuntime.ResolverFailed");
        }
    }

    private void LogRuntimeProfileSelection(DeviceStrategyRuntimeSelection selection)
    {
        var logKey = string.Join(
            "|",
            selection.SelectedPackage?.PackageId ?? string.Empty,
            selection.SelectionReasonKey,
            selection.PowerSourceMode,
            selection.PackageProfileIds.Count,
            selection.OverlappingProfileCount);
        if (string.Equals(_lastRuntimeProfileSelectionLogKey, logKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastRuntimeProfileSelectionLogKey = logKey;
        if (selection.SelectedPackage is { } package && selection.SelectedCompatibility is { } compatibility)
        {
            _logger.Info(
                $"Device strategy selected: {package.Name} ({package.PackageId}), " +
                $"compatibility={compatibility.Level}, powerSource={selection.PowerSourceMode}, " +
                $"packageProfiles={selection.PackageProfileIds.Count}, " +
                $"localOverrides={selection.OverlappingProfileCount}.");
            return;
        }

        _logger.Info(
            $"Device strategy fallback: {selection.SelectionReasonKey}; " +
            $"powerSource={selection.PowerSourceMode}, localProfiles={selection.Profiles.Count}.");
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
        => await _sessionCoordinator.RunExclusiveAsync(
            async () =>
            {
                try
                {
                    await _rollbackManager.RollbackAsync(cancellationToken);
                    if (await _rollbackManager.GetLastKnownStateAsync(cancellationToken) is not null)
                    {
                        _sessionCoordinator.ClearActive();
                        _logger.Warn("Rollback left unresolved state; the active scheduling session was cleared and new adjustments remain blocked.");
                        return false;
                    }

                    _sessionCoordinator.Reset();
                    _logger.Info("Rollback completed.");
                    return true;
                }
                catch
                {
                    // Do not let an unreadable or failed rollback leave an in-memory
                    // session eligible for a same-profile fast path on the next scan.
                    _sessionCoordinator.ClearActive();
                    throw;
                }
            },
            cancellationToken);

    public async Task<TargetExperienceAdaptiveResult?> AdaptTargetExperienceAsync(
        TargetExperienceControlDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (_targetExperienceAdaptiveCoordinator is null)
        {
            return null;
        }

        return await _sessionCoordinator.RunExclusiveAsync(async () =>
        {
            var activeSession = _sessionCoordinator.ActiveSession;
            if (activeSession is null)
            {
                return null;
            }

            var capabilities = await _capabilityDetector.DetectAsync(cancellationToken);
            var powerSourceMode = await _powerStatusProvider.GetCurrentPowerSourceAsync(cancellationToken);
            PowerPlanInfo? activePowerPlan = capabilities.SupportsPowerPlanSwitching
                ? await _powerPlanManager.GetActivePlanAsync(cancellationToken)
                : capabilities.AvailablePowerPlans.FirstOrDefault(plan => plan.IsActive);

            var profiles = await _profileManager.LoadProfilesAsync(cancellationToken);
            var profile = profiles.FirstOrDefault(p => p.Id == activeSession.ProfileId);
            if (profile is null)
            {
                return null;
            }

            var app = new FocusedAppContext
            {
                ProcessId = activeSession.ProcessId,
                ProcessName = activeSession.ProcessName,
                WindowTitle = string.Empty,
                WindowHandle = nint.Zero
            };

            return await _targetExperienceAdaptiveCoordinator.EvaluateAndApplyAsync(
                app,
                profile,
                capabilities,
                powerSourceMode,
                activePowerPlan,
                decision,
                cancellationToken);
        }, cancellationToken);
    }

    private async Task<bool> RestoreActiveSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _rollbackManager.RollbackAsync(cancellationToken);
            if (_targetExperienceAdaptiveCoordinator is not null)
            {
                await _targetExperienceAdaptiveCoordinator.ResetSessionAsync(cancellationToken);
            }
            _sessionCoordinator.ClearActive();

            if (await _rollbackManager.GetLastKnownStateAsync(cancellationToken) is null)
            {
                _logger.PublishUserEvent(
                    UserEventCode.PreviousSessionRestored,
                    AppLogLevel.Information,
                    "Previous scheduling session restored before applying the next profile.");
                return true;
            }

            _logger.PublishUserEvent(
                UserEventCode.PreviousSessionRollbackIncomplete,
                AppLogLevel.Warning,
                "Previous scheduling session has unresolved rollback items.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RollbackStateUnavailableException exception)
        {
            _sessionCoordinator.ClearActive();
            _logger.PublishUserEvent(
                UserEventCode.RollbackStateUnreadable,
                AppLogLevel.Error,
                $"Rollback state could not be read while changing sessions. Path: {exception.StateFilePath}");
            return false;
        }
        catch (Exception exception)
        {
            _sessionCoordinator.ClearActive();
            _logger.Error("Previous scheduling session rollback failed.", exception);
            _logger.PublishUserEvent(
                UserEventCode.PreviousSessionRollbackIncomplete,
                AppLogLevel.Error,
                "Previous scheduling session rollback failed; the next profile was blocked.");
            return false;
        }
    }

    private async Task<SchedulerRunResult> PersistForegroundUnavailableResultAsync(
        CapabilitySnapshot capabilities,
        string message,
        CancellationToken cancellationToken)
    {
        var recoveryCompleted = true;
        if (_sessionCoordinator.ActiveSession is not null)
        {
            // Focus can disappear while a process switch is in flight. Use an
            // independent token so a cancelled scan cannot leave the old
            // session applied after the window has already gone away.
            recoveryCompleted = await RestoreActiveSessionAsync(CancellationToken.None);
        }

        if (recoveryCompleted && _sessionCoordinator.ActiveSession is null)
        {
            try
            {
                recoveryCompleted = await _rollbackManager.GetLastKnownStateAsync(CancellationToken.None) is null;
            }
            catch (RollbackStateUnavailableException exception)
            {
                recoveryCompleted = false;
                _logger.PublishUserEvent(
                    UserEventCode.RollbackStateUnreadable,
                    AppLogLevel.Error,
                    $"Rollback state could not be read while the foreground app was unavailable. Path: {exception.StateFilePath}");
            }
        }

        var recoverySummary = recoveryCompleted
            ? "The active scheduling session was restored or no active session remained."
            : "The active scheduling session could not be fully restored; new adjustments remain blocked.";
        return await PersistResultAsync(new SchedulerRunResult
        {
            Capabilities = capabilities,
            Success = false,
            FailureRecoveryCompleted = recoveryCompleted,
            Summary = $"{message} {recoverySummary}"
        }, cancellationToken);
    }

    private async Task<bool> RestoreFailedRunAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.Warn($"Scheduling transaction {transactionId} failed. Attempting immediate rollback.");
            await _rollbackManager.RollbackAsync(cancellationToken);
            if (await _rollbackManager.GetLastKnownStateAsync(cancellationToken) is null)
            {
                _logger.PublishUserEvent(
                    UserEventCode.FailedRunRollbackCompleted,
                    AppLogLevel.Information,
                    $"Scheduling transaction {transactionId} was rolled back after failure.",
                    transactionId);
                return true;
            }

            _logger.PublishUserEvent(
                UserEventCode.FailedRunRollbackIncomplete,
                AppLogLevel.Warning,
                $"Scheduling transaction {transactionId} still has unresolved rollback items.",
                transactionId);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error($"Automatic rollback failed for scheduling transaction {transactionId}.", exception);
            _logger.PublishUserEvent(
                UserEventCode.AutomaticRollbackFailed,
                AppLogLevel.Error,
                $"Automatic rollback failed for scheduling transaction {transactionId}.",
                transactionId);
            return false;
        }
    }

    private async Task<SchedulerRunResult> PersistResultAsync(
        SchedulerRunResult result,
        CancellationToken cancellationToken)
    {
        await _runtimeStateStore.RecordRunAsync(result, cancellationToken);
        return result;
    }

    private async Task<SchedulerRunResult> PersistNewSessionResultAsync(
        SchedulerRunResult result,
        SchedulingSessionIdentity requestedSession,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Do not expose a session as active until its audit record exists.
            await _runtimeStateStore.RecordRunAsync(result, cancellationToken);
            _sessionCoordinator.MarkActive(requestedSession, transactionId);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The provider may already have changed state when cancellation is
            // observed. Roll back independently before propagating cancellation.
            await RestoreFailedRunAsync(transactionId, CancellationToken.None);
            _sessionCoordinator.ClearActive();
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error(
                $"Runtime history persistence failed for scheduling transaction {transactionId}; rolling back the new session.",
                exception);
            var recoveryCompleted = await RestoreFailedRunAsync(transactionId, CancellationToken.None);
            _sessionCoordinator.ClearActive();

            var recoverySummary = recoveryCompleted
                ? "Scheduling changes were rolled back because the runtime history could not be saved."
                : "Runtime history could not be saved and rollback is incomplete; new adjustments remain blocked.";
            return result with
            {
                Success = false,
                FailureRecoveryCompleted = recoveryCompleted,
                RuntimeStatePersistenceFailed = true,
                Summary = recoverySummary
            };
        }
    }
}
