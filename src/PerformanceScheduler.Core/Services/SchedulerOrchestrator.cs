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
    private readonly IPriorityManager _priorityManager;
    private readonly IProfileManager _profileManager;
    private readonly IRollbackManager _rollbackManager;
    private readonly IMetricsCollector _metricsCollector;
    private readonly IRuntimeStateStore _runtimeStateStore;
    private readonly IAppLogger _logger;
    private readonly ProfileMatcher _profileMatcher;

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
        ProfileMatcher? profileMatcher = null)
    {
        _foregroundWatcher = foregroundWatcher;
        _processInspector = processInspector;
        _capabilityDetector = capabilityDetector;
        _powerStatusProvider = powerStatusProvider;
        _powerPlanManager = powerPlanManager;
        _priorityManager = priorityManager;
        _profileManager = profileManager;
        _rollbackManager = rollbackManager;
        _metricsCollector = metricsCollector;
        _runtimeStateStore = runtimeStateStore;
        _logger = logger;
        _profileMatcher = profileMatcher ?? new ProfileMatcher();
    }

    public Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _capabilityDetector.DetectAsync(cancellationToken);

    public async Task<SchedulerRunResult> EvaluateForegroundAsync(CancellationToken cancellationToken = default)
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
        PowerPlanInfo? activePowerPlan = await _powerPlanManager.GetActivePlanAsync(cancellationToken);
        var previousPowerPlan = activePowerPlan;
        PowerPlanAdvancedState? previousAdvancedPowerSettings = null;
        PriorityAdjustmentResult? priorityResult = null;
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments = Array.Empty<BackgroundProcessAdjustmentResult>();

        if (match is null)
        {
            _logger.Info($"No profile matched for {app.ProcessName}.");
        }
        else
        {
            var originalPriorities = new Dictionary<int, PriorityLevel>();
            var originalForegroundPriority = await _priorityManager.TryGetPriorityAsync(app.ProcessId, cancellationToken);
            if (originalForegroundPriority is { } foregroundPriority)
            {
                originalPriorities[app.ProcessId] = foregroundPriority;
            }

            activePowerPlan = await ApplyPowerPlanAsync(match, activePowerPlan, cancellationToken);
            previousAdvancedPowerSettings = await ApplyAdvancedPowerSettingsAsync(match, activePowerPlan, powerSourceMode, cancellationToken);
            priorityResult = await _priorityManager.ApplyForegroundBoostAsync(app, match.Profile, cancellationToken);
            backgroundAdjustments = await _priorityManager.ApplyBackgroundPoliciesAsync(app, match.Profile, cancellationToken);
            CaptureAdjustedBackgroundPriorities(originalPriorities, backgroundAdjustments);
            await CaptureRollbackStateAsync(
                match,
                previousPowerPlan,
                previousAdvancedPowerSettings,
                originalPriorities,
                backgroundAdjustments,
                cancellationToken);

            foreach (var adjustment in backgroundAdjustments.Where(result => result.Status == SchedulerActionStatus.Applied))
            {
                _logger.Info(
                    $"Background policy applied: {adjustment.ProcessName} ({adjustment.ProcessId}) -> {adjustment.AppliedPriority} [{adjustment.Category}]");
            }

            _logger.Info($"Profile matched: {match.Profile.Name} (score: {match.Score}).");
        }

        var metrics = await _metricsCollector.CollectAsync(app, cancellationToken);
        var summary = match is null
            ? $"Foreground app {app.ProcessName} has no matching profile."
            : BuildSummary(app, match, backgroundAdjustments);

        return await PersistResultAsync(new SchedulerRunResult
        {
            ActiveApp = app,
            MatchResult = match,
            ActivePowerPlan = activePowerPlan,
            PriorityChange = priorityResult,
            BackgroundAdjustments = backgroundAdjustments,
            Metrics = metrics,
            Capabilities = capabilities,
            PowerSourceMode = powerSourceMode,
            Success = true,
            Summary = summary
        }, cancellationToken);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _rollbackManager.RollbackAsync(cancellationToken);
        _logger.Info("Rollback requested.");
    }

    private async Task<PowerPlanInfo?> ApplyPowerPlanAsync(
        ProfileMatchResult match,
        PowerPlanInfo? activePowerPlan,
        CancellationToken cancellationToken)
    {
        var desiredScheme = match.Profile.PowerPlan.SchemeGuid;
        if (desiredScheme is Guid targetScheme &&
            (activePowerPlan is null || activePowerPlan.SchemeGuid != targetScheme))
        {
            var switched = await _powerPlanManager.SetActivePlanAsync(targetScheme, cancellationToken);
            if (switched)
            {
                _logger.Info($"Power plan switched to {targetScheme}.");
                return await _powerPlanManager.GetActivePlanAsync(cancellationToken);
            }

            _logger.Warn($"Power plan switch failed for {targetScheme}.");
        }

        return activePowerPlan;
    }

    private async Task<PowerPlanAdvancedState?> ApplyAdvancedPowerSettingsAsync(
        ProfileMatchResult match,
        PowerPlanInfo? activePowerPlan,
        PowerSourceMode currentPowerSourceMode,
        CancellationToken cancellationToken)
    {
        if (!match.Profile.PowerPlan.Advanced.HasChanges || activePowerPlan is null)
        {
            return null;
        }

        var targetPowerSourceMode = match.Profile.PowerSourceMode == PowerSourceMode.Any
            ? currentPowerSourceMode
            : match.Profile.PowerSourceMode;
        var previousAdvancedSettings = await _powerPlanManager.GetAdvancedSettingsAsync(
            activePowerPlan.SchemeGuid,
            targetPowerSourceMode,
            cancellationToken);
        if (previousAdvancedSettings is null)
        {
            _logger.Warn($"Advanced power settings skipped because the current values could not be captured for {targetPowerSourceMode}.");
            return null;
        }

        var applied = await _powerPlanManager.ApplyAdvancedSettingsAsync(
            activePowerPlan.SchemeGuid,
            targetPowerSourceMode,
            match.Profile.PowerPlan.Advanced,
            cancellationToken);

        if (applied)
        {
            _logger.Info($"Advanced power settings applied for {targetPowerSourceMode}.");
            return previousAdvancedSettings;
        }

        _logger.Warn($"Advanced power settings failed for {targetPowerSourceMode}.");
        return null;
    }

    private async Task CaptureRollbackStateAsync(
        ProfileMatchResult match,
        PowerPlanInfo? previousPowerPlan,
        PowerPlanAdvancedState? previousAdvancedPowerSettings,
        IReadOnlyDictionary<int, PriorityLevel> originalPriorities,
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments,
        CancellationToken cancellationToken)
    {
        var shouldCaptureState = match.Profile.PowerPlan.RestoreOnExit ||
                                 previousAdvancedPowerSettings is not null ||
                                 match.Profile.Priority.ForegroundPriority != PriorityLevel.Normal ||
                                 backgroundAdjustments.Any(result => result.Status == SchedulerActionStatus.Applied);

        if (!shouldCaptureState)
        {
            return;
        }

        await _rollbackManager.CaptureAsync(new SchedulerState
        {
            PreviousPowerPlan = previousPowerPlan,
            PreviousAdvancedPowerSettings = previousAdvancedPowerSettings,
            OriginalPriorities = new Dictionary<int, PriorityLevel>(originalPriorities)
        }, cancellationToken);
    }

    private static void CaptureAdjustedBackgroundPriorities(
        IDictionary<int, PriorityLevel> originalPriorities,
        IEnumerable<BackgroundProcessAdjustmentResult> backgroundAdjustments)
    {
        foreach (var adjustment in backgroundAdjustments)
        {
            if (adjustment.Status != SchedulerActionStatus.Applied || adjustment.PreviousPriority is not { } previousPriority)
            {
                continue;
            }

            originalPriorities.TryAdd(adjustment.ProcessId, previousPriority);
        }
    }

    private static string BuildSummary(
        FocusedAppContext app,
        ProfileMatchResult match,
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments)
    {
        var appliedCount = backgroundAdjustments.Count(result => result.Status == SchedulerActionStatus.Applied);
        return appliedCount == 0
            ? $"Applied profile {match.Profile.Name} to {app.ProcessName}."
            : $"Applied profile {match.Profile.Name} to {app.ProcessName} and adjusted {appliedCount} background process(es).";
    }

    private async Task<SchedulerRunResult> PersistResultAsync(
        SchedulerRunResult result,
        CancellationToken cancellationToken)
    {
        await _runtimeStateStore.RecordRunAsync(result, cancellationToken);
        return result;
    }
}
