using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Abstractions;

public interface ITargetExperienceAdaptiveCoordinator
{
    Task<TargetExperienceAdaptiveResult?> EvaluateAndApplyAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        CapabilitySnapshot capabilities,
        PowerSourceMode powerSourceMode,
        PowerPlanInfo? activePowerPlan,
        TargetExperienceControlDecision decision,
        CancellationToken cancellationToken = default);

    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}
