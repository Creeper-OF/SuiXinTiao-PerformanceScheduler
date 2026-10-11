using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IPriorityManager
{
    Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessIdentitySnapshot?>(null);

    Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default);

    Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default);

    Task<ProcessEfficiencyModeState?> TryGetEfficiencyModeAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessEfficiencyModeState?>(null);

    Task<bool> SetEfficiencyModeAsync(
        int processId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    async Task<ConditionalProcessUpdateResult> RestorePriorityIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        PriorityLevel expectedCurrentPriority,
        PriorityLevel originalPriority,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var currentPriority = await TryGetPriorityAsync(identity.ProcessId, cancellationToken);
        if (currentPriority != expectedCurrentPriority)
        {
            return currentPriority is null
                ? ConditionalProcessUpdateResult.Failed
                : ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        return await SetPriorityAsync(identity.ProcessId, originalPriority, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }

    async Task<ConditionalProcessUpdateResult> RestoreEfficiencyModeIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        bool expectedCurrentState,
        bool originalState,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var currentState = await TryGetEfficiencyModeAsync(identity.ProcessId, cancellationToken);
        if (currentState?.IsEnabled != expectedCurrentState)
        {
            return currentState is null
                ? ConditionalProcessUpdateResult.Failed
                : ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        return await SetEfficiencyModeAsync(identity.ProcessId, originalState, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }

    Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        ProcessIdentitySnapshot expectedIdentity,
        CancellationToken cancellationToken = default) =>
        ApplyForegroundBoostAsync(app, profile, cancellationToken);

    Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        IReadOnlyList<BackgroundProcessPriorityBaseline> preparedBaselines,
        CancellationToken cancellationToken = default) =>
        ApplyBackgroundPoliciesAsync(foregroundApp, profile, cancellationToken);
}
