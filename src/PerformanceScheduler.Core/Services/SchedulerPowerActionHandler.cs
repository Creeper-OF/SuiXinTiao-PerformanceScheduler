using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

internal sealed class SchedulerPowerActionHandler
{
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IAppLogger _logger;

    public SchedulerPowerActionHandler(IPowerPlanManager powerPlanManager, IAppLogger logger)
    {
        _powerPlanManager = powerPlanManager;
        _logger = logger;
    }

    public async Task<PowerPlanActionExecution> ApplyPowerPlanAsync(
        SchedulerActionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var match = request.Match;
        var activePowerPlan = request.ActivePowerPlan;
        var desiredScheme = match.Profile.PowerPlan.SchemeGuid;
        if (!request.Capabilities.SupportsRollback ||
            !request.Capabilities.SupportsPowerPlanSwitching)
        {
            if (desiredScheme is null && !match.Profile.PowerPlan.Advanced.HasChanges)
            {
                return new PowerPlanActionExecution { ActivePowerPlan = activePowerPlan };
            }

            const string message = "Power plan adjustments skipped because this device does not support safe power plan switching.";
            _logger.Warn(message);
            var reasonCode = request.Capabilities.SupportsRollback
                ? SchedulerActionReasonCode.CapabilityUnavailable
                : SchedulerActionReasonCode.SafeRollbackUnavailable;
            var unsupportedActions = desiredScheme is { } targetScheme
                ? new[]
                {
                    SchedulerActionPlanFactory.Complete(
                        SchedulerActionPlanFactory.CreatePowerPlanSwitch(
                            match.Profile,
                            activePowerPlan,
                            targetScheme),
                        SchedulerActionStatus.Unsupported,
                        message,
                        reasonCode)
                }
                : Array.Empty<SchedulerActionResult>();
            return new PowerPlanActionExecution
            {
                ActivePowerPlan = activePowerPlan,
                Actions = unsupportedActions
            };
        }

        if (desiredScheme is not Guid targetSchemeGuid)
        {
            return new PowerPlanActionExecution { ActivePowerPlan = activePowerPlan };
        }

        var plan = SchedulerActionPlanFactory.CreatePowerPlanSwitch(
            match.Profile,
            activePowerPlan,
            targetSchemeGuid);
        if (activePowerPlan is null)
        {
            const string message = "Power plan switch was blocked because the original active plan could not be captured safely.";
            _logger.Warn(message);
            return new PowerPlanActionExecution
            {
                Actions =
                [
                    SchedulerActionPlanFactory.Complete(
                        plan,
                        SchedulerActionStatus.Failed,
                        message,
                        SchedulerActionReasonCode.BaselineCaptureFailed)
                ]
            };
        }

        if (activePowerPlan.SchemeGuid == targetSchemeGuid)
        {
            return new PowerPlanActionExecution
            {
                ActivePowerPlan = activePowerPlan,
                Actions =
                [
                    SchedulerActionPlanFactory.Complete(
                        plan,
                        SchedulerActionStatus.Skipped,
                        "The requested power plan is already active.",
                        SchedulerActionReasonCode.TargetAlreadyActive)
                ]
            };
        }

        var switched = await _powerPlanManager.SetActivePlanAsync(targetSchemeGuid, cancellationToken);
        if (switched)
        {
            var message = $"Power plan switched to {targetSchemeGuid}.";
            _logger.Info(message);
            return new PowerPlanActionExecution
            {
                ActivePowerPlan = await _powerPlanManager.GetActivePlanAsync(cancellationToken),
                Actions =
                [
                    SchedulerActionPlanFactory.Complete(
                        plan,
                        SchedulerActionStatus.Applied,
                        message)
                ]
            };
        }

        var failureMessage = $"Power plan switch failed for {targetSchemeGuid}.";
        _logger.Warn(failureMessage);
        return new PowerPlanActionExecution
        {
            ActivePowerPlan = activePowerPlan,
            Actions =
            [
                SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Failed,
                    failureMessage,
                    SchedulerActionReasonCode.ProviderApplyFailed)
            ]
        };
    }

    public async Task<AdvancedPowerActionPreparation> PrepareAdvancedAsync(
        SchedulerActionExecutionRequest request,
        PowerPlanInfo? activePowerPlan,
        CancellationToken cancellationToken)
    {
        var match = request.Match;
        if (!match.Profile.PowerPlan.Advanced.HasChanges)
        {
            return AdvancedPowerActionPreparation.Empty;
        }

        var targetPowerSourceMode = ResolvePowerSourceMode(match.Profile, request.PowerSourceMode);
        if (request.Capabilities.SupportsRollback &&
            request.Capabilities.SupportsPowerPlanSwitching &&
            CanApplyAdvancedPowerSettings(match, activePowerPlan))
        {
            var previousState = await _powerPlanManager.GetAdvancedSettingsAsync(
                activePowerPlan!.SchemeGuid,
                targetPowerSourceMode,
                cancellationToken);
            var plan = SchedulerActionPlanFactory.CreateAdvancedPowerSettings(
                match.Profile,
                previousState,
                activePowerPlan.SchemeGuid,
                targetPowerSourceMode);
            if (previousState is null)
            {
                var message = $"Advanced power settings skipped because the current values could not be captured for {targetPowerSourceMode}.";
                _logger.Warn(message);
                return new AdvancedPowerActionPreparation
                {
                    Plan = plan,
                    ImmediateResult = SchedulerActionPlanFactory.Complete(
                        plan,
                        SchedulerActionStatus.Failed,
                        "Advanced power settings were not applied because the original values could not be captured safely.",
                        SchedulerActionReasonCode.BaselineCaptureFailed)
                };
            }

            return new AdvancedPowerActionPreparation
            {
                Plan = plan,
                TargetPowerSourceMode = targetPowerSourceMode,
                PreviousState = previousState,
                AppliedState = CreateAppliedState(previousState, match.Profile.PowerPlan.Advanced),
                CanApply = true
            };
        }

        var unavailablePlan = SchedulerActionPlanFactory.CreateAdvancedPowerSettings(
            match.Profile,
            originalState: null,
            activePowerPlan?.SchemeGuid,
            targetPowerSourceMode);
        if (request.Capabilities.SupportsRollback && request.Capabilities.SupportsPowerPlanSwitching)
        {
            const string message = "Advanced power settings skipped because the desired power plan is not active.";
            _logger.Warn(message);
            return new AdvancedPowerActionPreparation
            {
                Plan = unavailablePlan,
                ImmediateResult = SchedulerActionPlanFactory.Complete(
                    unavailablePlan,
                    SchedulerActionStatus.Skipped,
                    message,
                    SchedulerActionReasonCode.TargetNotActive)
            };
        }

        return new AdvancedPowerActionPreparation
        {
            Plan = unavailablePlan,
            ImmediateResult = SchedulerActionPlanFactory.Complete(
                unavailablePlan,
                SchedulerActionStatus.Unsupported,
                "Advanced power settings are not supported by the current safe provider.",
                request.Capabilities.SupportsRollback
                    ? SchedulerActionReasonCode.CapabilityUnavailable
                    : SchedulerActionReasonCode.SafeRollbackUnavailable)
        };
    }

    public async Task<SchedulerActionResult?> ApplyAdvancedAsync(
        SchedulerActionExecutionRequest request,
        PowerPlanInfo? activePowerPlan,
        AdvancedPowerActionPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (preparation.ImmediateResult is not null)
        {
            return preparation.ImmediateResult;
        }

        if (!preparation.CanApply || preparation.Plan is null || activePowerPlan is null)
        {
            return null;
        }

        var applied = await _powerPlanManager.ApplyAdvancedSettingsAsync(
            activePowerPlan.SchemeGuid,
            preparation.TargetPowerSourceMode,
            request.Match.Profile.PowerPlan.Advanced,
            cancellationToken);
        if (applied)
        {
            var message = $"Advanced power settings applied for {preparation.TargetPowerSourceMode}.";
            _logger.Info(message);
            return SchedulerActionPlanFactory.Complete(
                preparation.Plan,
                SchedulerActionStatus.Applied,
                message);
        }

        var failureMessage = $"Advanced power settings failed for {preparation.TargetPowerSourceMode}.";
        _logger.Warn(failureMessage);
        return SchedulerActionPlanFactory.Complete(
            preparation.Plan,
            SchedulerActionStatus.Failed,
            failureMessage,
            SchedulerActionReasonCode.ProviderApplyFailed);
    }

    private static bool CanApplyAdvancedPowerSettings(
        ProfileMatchResult match,
        PowerPlanInfo? activePowerPlan) =>
        activePowerPlan is not null &&
        (match.Profile.PowerPlan.SchemeGuid is not { } desiredScheme ||
         activePowerPlan.SchemeGuid == desiredScheme);

    private static PowerPlanAdvancedState CreateAppliedState(
        PowerPlanAdvancedState baseline,
        PowerPlanAdvancedPreference preference) =>
        new()
        {
            SchemeGuid = baseline.SchemeGuid,
            PowerSourceMode = baseline.PowerSourceMode,
            ProcessorMaxStatePercent = preference.ProcessorMaxStatePercent ?? baseline.ProcessorMaxStatePercent
        };

    private static PowerSourceMode ResolvePowerSourceMode(
        PerformanceProfile profile,
        PowerSourceMode currentPowerSourceMode) =>
        profile.PowerSourceMode == PowerSourceMode.Any
            ? currentPowerSourceMode
            : profile.PowerSourceMode;
}

internal sealed record PowerPlanActionExecution
{
    public PowerPlanInfo? ActivePowerPlan { get; init; }

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();
}

internal sealed record AdvancedPowerActionPreparation
{
    public static AdvancedPowerActionPreparation Empty { get; } = new();

    public SchedulerActionPlan? Plan { get; init; }

    public PowerSourceMode TargetPowerSourceMode { get; init; } = PowerSourceMode.Any;

    public PowerPlanAdvancedState? PreviousState { get; init; }

    public PowerPlanAdvancedState? AppliedState { get; init; }

    public SchedulerActionResult? ImmediateResult { get; init; }

    public bool CanApply { get; init; }
}
