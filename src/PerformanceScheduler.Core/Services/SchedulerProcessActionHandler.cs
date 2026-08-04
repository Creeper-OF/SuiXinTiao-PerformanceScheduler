using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

internal sealed class SchedulerProcessActionHandler
{
    private readonly IPriorityManager _priorityManager;
    private readonly IAppLogger _logger;

    public SchedulerProcessActionHandler(IPriorityManager priorityManager, IAppLogger logger)
    {
        _priorityManager = priorityManager;
        _logger = logger;
    }

    public async Task<ProcessActionPreparation> PrepareAsync(
        SchedulerActionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Capabilities.SupportsRollback || !request.Capabilities.SupportsPriorityBoost)
        {
            return ProcessActionPreparation.Empty;
        }

        var app = request.ActiveApp;
        var profile = request.Match.Profile;
        var foregroundIdentity = await _priorityManager.TryGetProcessIdentityAsync(
            app.ProcessId,
            cancellationToken);
        var originalForegroundPriority = await _priorityManager.TryGetPriorityAsync(
            app.ProcessId,
            cancellationToken);
        var originalPriorities = new Dictionary<int, PriorityLevel>();
        var appliedPriorities = new Dictionary<int, PriorityLevel>();
        var processIdentities = new Dictionary<int, ProcessIdentitySnapshot>();
        var originalEfficiencyModes = new Dictionary<int, ProcessEfficiencyModeState>();
        var appliedEfficiencyModes = new Dictionary<int, bool>();

        if (foregroundIdentity is not null &&
            originalForegroundPriority is { } foregroundPriority &&
            foregroundPriority != profile.Priority.ForegroundPriority)
        {
            originalPriorities[app.ProcessId] = foregroundPriority;
            appliedPriorities[app.ProcessId] = profile.Priority.ForegroundPriority;
            processIdentities[app.ProcessId] = foregroundIdentity;
        }

        var backgroundBaselines = await _priorityManager.CaptureBackgroundPolicyBaselinesAsync(
            app,
            profile,
            cancellationToken);
        foreach (var baseline in backgroundBaselines)
        {
            processIdentities.TryAdd(baseline.ProcessId, baseline.Identity);
            if (baseline.PreviousPriority != baseline.TargetPriority)
            {
                originalPriorities.TryAdd(baseline.ProcessId, baseline.PreviousPriority);
                appliedPriorities.TryAdd(baseline.ProcessId, baseline.TargetPriority);
            }

            if (baseline.PreviousEfficiencyMode is not null &&
                baseline.PreviousEfficiencyMode.IsEnabled != baseline.TargetEfficiencyMode)
            {
                originalEfficiencyModes.TryAdd(baseline.ProcessId, baseline.PreviousEfficiencyMode);
                appliedEfficiencyModes.TryAdd(baseline.ProcessId, baseline.TargetEfficiencyMode);
            }
        }

        return new ProcessActionPreparation
        {
            ForegroundIdentity = foregroundIdentity,
            OriginalForegroundPriority = originalForegroundPriority,
            BackgroundBaselines = backgroundBaselines,
            OriginalPriorities = originalPriorities,
            AppliedPriorities = appliedPriorities,
            ProcessIdentities = processIdentities,
            OriginalEfficiencyModes = originalEfficiencyModes,
            AppliedEfficiencyModes = appliedEfficiencyModes
        };
    }

    public async Task<ProcessActionExecution> ApplyAsync(
        SchedulerActionExecutionRequest request,
        ProcessActionPreparation preparation,
        CancellationToken cancellationToken)
    {
        var app = request.ActiveApp;
        var profile = request.Match.Profile;
        PriorityAdjustmentResult? priorityResult;
        SchedulerActionReasonCode foregroundReasonCode;
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments =
            Array.Empty<BackgroundProcessAdjustmentResult>();

        if (!request.Capabilities.SupportsRollback)
        {
            const string message = "Profile matched, but adjustments were blocked because rollback is not supported on this device.";
            _logger.Warn(message);
            priorityResult = SchedulerResultSummaryBuilder.CreateUnsupportedPriorityResult(app.ProcessId, message);
            foregroundReasonCode = SchedulerActionReasonCode.SafeRollbackUnavailable;
        }
        else if (!request.Capabilities.SupportsPriorityBoost)
        {
            const string message = "Process priority adjustments are not supported on this device.";
            _logger.Warn(message);
            priorityResult = SchedulerResultSummaryBuilder.CreateUnsupportedPriorityResult(app.ProcessId, message);
            foregroundReasonCode = SchedulerActionReasonCode.CapabilityUnavailable;
        }
        else
        {
            if (preparation.ForegroundIdentity is null ||
                preparation.OriginalForegroundPriority is null)
            {
                priorityResult = new PriorityAdjustmentResult
                {
                    ProcessId = app.ProcessId,
                    Status = SchedulerActionStatus.Failed,
                    Message = "Foreground process identity or original priority could not be captured safely; priority adjustment was skipped."
                };
                foregroundReasonCode = SchedulerActionReasonCode.ProcessStateUnavailable;
            }
            else
            {
                priorityResult = await _priorityManager.ApplyForegroundBoostAsync(
                    app,
                    profile,
                    preparation.ForegroundIdentity,
                    cancellationToken);
                foregroundReasonCode = ResolveProviderReasonCode(priorityResult.Status);
            }

            backgroundAdjustments = await _priorityManager.ApplyBackgroundPoliciesAsync(
                app,
                profile,
                preparation.BackgroundBaselines,
                cancellationToken);
        }

        var actionResults = new List<SchedulerActionResult>();
        var foregroundPlan = SchedulerActionPlanFactory.CreateForegroundPriority(
            profile,
            app,
            preparation.OriginalForegroundPriority ?? priorityResult?.PreviousPriority);
        actionResults.Add(SchedulerActionPlanFactory.Complete(
            foregroundPlan,
            priorityResult?.Status ?? SchedulerActionStatus.Unsupported,
            priorityResult?.Message ?? "Foreground priority adjustment was not available.",
            priorityResult is null
                ? SchedulerActionReasonCode.ProviderResultMissing
                : foregroundReasonCode));
        AddBackgroundActionResults(
            actionResults,
            profile,
            preparation.BackgroundBaselines,
            backgroundAdjustments);

        foreach (var adjustment in backgroundAdjustments.Where(
                     result => result.Status == SchedulerActionStatus.Applied))
        {
            _logger.Info(
                $"Background policy applied: {adjustment.ProcessName} ({adjustment.ProcessId}) -> {adjustment.AppliedPriority} [{adjustment.Category}]");
        }

        return new ProcessActionExecution
        {
            PriorityChange = priorityResult,
            BackgroundAdjustments = backgroundAdjustments,
            Actions = actionResults
        };
    }

    private static void AddBackgroundActionResults(
        ICollection<SchedulerActionResult> results,
        PerformanceProfile profile,
        IReadOnlyList<BackgroundProcessPriorityBaseline> baselines,
        IReadOnlyList<BackgroundProcessAdjustmentResult> adjustments)
    {
        var adjustmentByProcessId = adjustments
            .GroupBy(item => item.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var baseline in baselines)
        {
            adjustmentByProcessId.TryGetValue(baseline.ProcessId, out var adjustment);
            var status = adjustment?.Status ?? SchedulerActionStatus.Failed;
            var message = adjustment?.Message ?? "No result was returned for the prepared background action.";
            var reasonCode = adjustment is null
                ? SchedulerActionReasonCode.ProviderResultMissing
                : ResolveProviderReasonCode(adjustment.Status);
            foreach (var plan in SchedulerActionPlanFactory.CreateBackgroundPlans(profile, baseline))
            {
                results.Add(SchedulerActionPlanFactory.Complete(plan, status, message, reasonCode));
            }
        }
    }

    private static SchedulerActionReasonCode ResolveProviderReasonCode(SchedulerActionStatus status) =>
        status switch
        {
            SchedulerActionStatus.Unsupported => SchedulerActionReasonCode.SafetyPolicyBlocked,
            SchedulerActionStatus.Failed => SchedulerActionReasonCode.ProviderApplyFailed,
            _ => SchedulerActionReasonCode.None
        };
}

internal sealed record ProcessActionPreparation
{
    public static ProcessActionPreparation Empty { get; } = new();

    public ProcessIdentitySnapshot? ForegroundIdentity { get; init; }

    public PriorityLevel? OriginalForegroundPriority { get; init; }

    public IReadOnlyList<BackgroundProcessPriorityBaseline> BackgroundBaselines { get; init; } =
        Array.Empty<BackgroundProcessPriorityBaseline>();

    public IReadOnlyDictionary<int, PriorityLevel> OriginalPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();

    public IReadOnlyDictionary<int, PriorityLevel> AppliedPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();

    public IReadOnlyDictionary<int, ProcessIdentitySnapshot> ProcessIdentities { get; init; } =
        new Dictionary<int, ProcessIdentitySnapshot>();

    public IReadOnlyDictionary<int, ProcessEfficiencyModeState> OriginalEfficiencyModes { get; init; } =
        new Dictionary<int, ProcessEfficiencyModeState>();

    public IReadOnlyDictionary<int, bool> AppliedEfficiencyModes { get; init; } =
        new Dictionary<int, bool>();
}

internal sealed record ProcessActionExecution
{
    public PriorityAdjustmentResult? PriorityChange { get; init; }

    public IReadOnlyList<BackgroundProcessAdjustmentResult> BackgroundAdjustments { get; init; } =
        Array.Empty<BackgroundProcessAdjustmentResult>();

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();
}
