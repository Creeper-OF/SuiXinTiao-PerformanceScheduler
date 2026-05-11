using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class PowerPlanOptionViewModel
{
    public PowerPlanOptionViewModel(Guid? schemeGuid, string displayName)
    {
        SchemeGuid = schemeGuid;
        DisplayName = displayName;
    }

    public Guid? SchemeGuid { get; }

    public string DisplayName { get; }

    public static PowerPlanOptionViewModel FromModel(PowerPlanInfo plan) =>
        new(plan.SchemeGuid, plan.Name);
}
