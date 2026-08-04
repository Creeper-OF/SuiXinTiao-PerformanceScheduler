using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class SchedulerActionExecutor : ISchedulerActionExecutor
{
    private readonly IRollbackManager _rollbackManager;
    private readonly IAppLogger _logger;
    private readonly SchedulerPowerActionHandler _powerHandler;
    private readonly SchedulerProcessActionHandler _processHandler;

    public SchedulerActionExecutor(
        IPowerPlanManager powerPlanManager,
        IPriorityManager priorityManager,
        IRollbackManager rollbackManager,
        IAppLogger logger)
    {
        _rollbackManager = rollbackManager;
        _logger = logger;
        _powerHandler = new SchedulerPowerActionHandler(powerPlanManager, logger);
        _processHandler = new SchedulerProcessActionHandler(priorityManager, logger);
    }

    public async Task<SchedulerActionExecutionResult> ExecuteAsync(
        SchedulerActionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var processPreparation = await _processHandler.PrepareAsync(request, cancellationToken);
        if (request.Capabilities.SupportsRollback)
        {
            await CaptureRollbackStateAsync(
                request,
                request.Capabilities.SupportsPowerPlanSwitching
                    ? request.ActivePowerPlan
                    : null,
                previousAdvancedPowerSettings: null,
                appliedAdvancedPowerSettings: null,
                processPreparation,
                cancellationToken);
        }

        var powerExecution = await _powerHandler.ApplyPowerPlanAsync(request, cancellationToken);
        var advancedPreparation = await _powerHandler.PrepareAdvancedAsync(
            request,
            powerExecution.ActivePowerPlan,
            cancellationToken);
        if (advancedPreparation.PreviousState is not null)
        {
            await CaptureRollbackStateAsync(
                request,
                request.ActivePowerPlan,
                advancedPreparation.PreviousState,
                advancedPreparation.AppliedState,
                processPreparation,
                cancellationToken);
        }

        var actionResults = new List<SchedulerActionResult>(powerExecution.Actions);
        var advancedResult = await _powerHandler.ApplyAdvancedAsync(
            request,
            powerExecution.ActivePowerPlan,
            advancedPreparation,
            cancellationToken);
        if (advancedResult is not null)
        {
            actionResults.Add(advancedResult);
        }

        var processExecution = await _processHandler.ApplyAsync(
            request,
            processPreparation,
            cancellationToken);
        actionResults.AddRange(processExecution.Actions);

        _logger.Info($"Profile matched: {request.Match.Profile.Name} (score: {request.Match.Score}).");
        return new SchedulerActionExecutionResult
        {
            ActivePowerPlan = powerExecution.ActivePowerPlan,
            PriorityChange = processExecution.PriorityChange,
            BackgroundAdjustments = processExecution.BackgroundAdjustments,
            Actions = actionResults
        };
    }

    private async Task CaptureRollbackStateAsync(
        SchedulerActionExecutionRequest request,
        PowerPlanInfo? previousPowerPlan,
        PowerPlanAdvancedState? previousAdvancedPowerSettings,
        PowerPlanAdvancedState? appliedAdvancedPowerSettings,
        ProcessActionPreparation processPreparation,
        CancellationToken cancellationToken)
    {
        var powerPreference = request.Match.Profile.PowerPlan;
        var shouldRestorePowerPlan = powerPreference.RestoreOnExit &&
                                     previousPowerPlan is not null &&
                                     powerPreference.SchemeGuid is { } targetScheme &&
                                     previousPowerPlan.SchemeGuid != targetScheme;
        var appliedPowerPlan = shouldRestorePowerPlan
            ? new PowerPlanInfo(
                powerPreference.SchemeGuid!.Value,
                powerPreference.PreferredPlanName,
                IsActive: false)
            : null;
        var shouldCaptureState = shouldRestorePowerPlan ||
                                 previousAdvancedPowerSettings is not null ||
                                 processPreparation.OriginalPriorities.Count > 0 ||
                                 processPreparation.OriginalEfficiencyModes.Count > 0;
        if (!shouldCaptureState)
        {
            return;
        }

        await _rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = request.TransactionId,
            PreviousPowerPlan = shouldRestorePowerPlan ? previousPowerPlan : null,
            AppliedPowerPlan = appliedPowerPlan,
            PreviousAdvancedPowerSettings = previousAdvancedPowerSettings,
            AppliedAdvancedPowerSettings = appliedAdvancedPowerSettings,
            OriginalPriorities = new Dictionary<int, PriorityLevel>(processPreparation.OriginalPriorities),
            AppliedPriorities = new Dictionary<int, PriorityLevel>(processPreparation.AppliedPriorities),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot>(processPreparation.ProcessIdentities),
            OriginalEfficiencyModes = new Dictionary<int, ProcessEfficiencyModeState>(
                processPreparation.OriginalEfficiencyModes),
            AppliedEfficiencyModes = new Dictionary<int, bool>(processPreparation.AppliedEfficiencyModes)
        }, cancellationToken);
    }
}
