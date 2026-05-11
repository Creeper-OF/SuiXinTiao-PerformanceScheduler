using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IPowerPlanManager
{
    Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default);

    Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default);

    Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default);

    Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
        Guid schemeGuid,
        PowerSourceMode powerSourceMode,
        CancellationToken cancellationToken = default);

    Task<bool> ApplyAdvancedSettingsAsync(
        Guid schemeGuid,
        PowerSourceMode powerSourceMode,
        PowerPlanAdvancedPreference preference,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreAdvancedSettingsAsync(
        PowerPlanAdvancedState state,
        CancellationToken cancellationToken = default);
}
