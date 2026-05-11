using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IPriorityManager
{
    Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default);

    Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default);

    Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default);
}
