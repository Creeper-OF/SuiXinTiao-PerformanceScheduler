using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

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
    private readonly SchedulingSessionCoordinator _sessionCoordinator;
    private readonly Func<DateTimeOffset> _utcNow;

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
        ISchedulerActionExecutor? actionExecutor = null)
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
            logger);
        _profileMatcher = profileMatcher ?? new ProfileMatcher();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _sessionCoordinator = new SchedulingSessionCoordinator(
            failureRetryBaseDelay,
            failureRetryMaxDelay);
    }

    public Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _capabilityDetector.DetectAsync(cancellationToken);

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
            return await PersistResultAsync(new SchedulerRunResult
            {
                Capabilities = capabilities,
                Success = false,
                Summary = message
            }, cancellationToken);
        }

        var app = await _processInspector.InspectAsync(foregroundWindow, cancellationToken);
        if (app is null)
        {
            var message = $"Unable to inspect process {foregroundWindow.ProcessId}.";
            _logger.Warn(message);
            return await PersistResultAsync(new SchedulerRunResult
            {
                Capabilities = capabilities,
                Success = false,
                Summary = message
            }, cancellationToken);
        }

        var powerSourceMode = await _powerStatusProvider.GetCurrentPowerSourceAsync(cancellationToken);
        _logger.Info($"Foreground app detected: {app.ProcessName} ({app.ProcessId})");
        app = app with { PowerSourceMode = powerSourceMode };

        var profiles = await _profileManager.LoadProfilesAsync(cancellationToken);
        var match = _profileMatcher.Match(app, profiles);
        var requestedSession = match is null
            ? null
            : SchedulingSessionIdentity.Create(app, match.Profile, powerSourceMode);

        if (_sessionCoordinator.RequiresTransition(requestedSession))
        {
            var restored = await RestoreActiveSessionAsync(cancellationToken);
            if (!restored)
            {
                return await PersistResultAsync(new SchedulerRunResult
                {
                    ActiveApp = app,
                    MatchResult = match,
                    Capabilities = capabilities,
                    PowerSourceMode = powerSourceMode,
                    Success = false,
                    FailureRecoveryCompleted = false,
                    Summary = "The previous scheduling session could not be fully restored. New adjustments were blocked."
                }, cancellationToken);
            }
        }

        if (requestedSession is not null && _sessionCoordinator.ActiveSession is null &&
            await _rollbackManager.GetLastKnownStateAsync(cancellationToken) is not null)
        {
            const string message = "Pending rollback state exists. New adjustments were blocked until recovery succeeds.";
            _logger.PublishUserEvent(
                UserEventCode.PendingRollbackBlocked,
                AppLogLevel.Warning,
                message);
            return await PersistResultAsync(new SchedulerRunResult
            {
                ActiveApp = app,
                MatchResult = match,
                Capabilities = capabilities,
                PowerSourceMode = powerSourceMode,
                Success = false,
                FailureRecoveryCompleted = false,
                Summary = message
            }, cancellationToken);
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
            return await PersistResultAsync(new SchedulerRunResult
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
            }, cancellationToken);
        }

        if (requestedSession is not null && _sessionCoordinator.IsCurrent(requestedSession))
        {
            var currentMetrics = capabilities.SupportsMetricsCollection
                ? await _metricsCollector.CollectAsync(app, cancellationToken)
                : null;
            return await PersistResultAsync(new SchedulerRunResult
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
            }, cancellationToken);
        }

        var transactionId = requestedSession is null ? (Guid?)null : Guid.NewGuid();
        PriorityAdjustmentResult? priorityResult = null;
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments =
            Array.Empty<BackgroundProcessAdjustmentResult>();
        IReadOnlyList<SchedulerActionResult> actionResults = Array.Empty<SchedulerActionResult>();

        if (match is null)
        {
            _logger.Info($"No profile matched for {app.ProcessName}.");
        }
        else
        {
            var execution = await _actionExecutor.ExecuteAsync(new SchedulerActionExecutionRequest
            {
                TransactionId = transactionId!.Value,
                ActiveApp = app,
                Match = match,
                Capabilities = capabilities,
                PowerSourceMode = powerSourceMode,
                ActivePowerPlan = activePowerPlan
            }, cancellationToken);
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
                cancellationToken);
            failureState = _sessionCoordinator.MarkFailed(requestedSession!, _utcNow());
        }
        else if (match is not null && capabilities.SupportsRollback)
        {
            _sessionCoordinator.MarkActive(requestedSession!, transactionId!.Value);
        }

        var metrics = capabilities.SupportsMetricsCollection
            ? await _metricsCollector.CollectAsync(app, cancellationToken)
            : null;
        var summary = match is null
            ? $"Foreground app {app.ProcessName} has no matching profile."
            : runSucceeded
                ? SchedulerResultSummaryBuilder.Build(app, match, backgroundAdjustments, capabilities, priorityResult)
                : SchedulerResultSummaryBuilder.BuildFailure(
                    app,
                    match,
                    failedActions,
                    failureRecoveryCompleted);

        return await PersistResultAsync(new SchedulerRunResult
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
            Success = runSucceeded,
            ConsecutiveFailureCount = failureState?.ConsecutiveFailureCount ?? 0,
            RetryAfter = failureState?.RetryAfter,
            FailureRecoveryCompleted = runSucceeded ? null : failureRecoveryCompleted,
            Summary = summary
        }, cancellationToken);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
        => await _sessionCoordinator.RunExclusiveAsync(
            async () =>
            {
                await _rollbackManager.RollbackAsync(cancellationToken);
                _sessionCoordinator.Reset();
                _logger.Info("Rollback requested.");
            },
            cancellationToken);

    private async Task<bool> RestoreActiveSessionAsync(CancellationToken cancellationToken)
    {
        await _rollbackManager.RollbackAsync(cancellationToken);
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
}
