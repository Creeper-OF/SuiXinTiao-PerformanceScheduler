namespace PerformanceScheduler.Core.Models;

public enum SchedulerActionKind
{
    PowerPlanSwitch,
    AdvancedPowerSettings,
    ForegroundPriority,
    BackgroundPriority,
    BackgroundEfficiencyMode,
    ForegroundCpuAffinity,
    BackgroundCpuAffinity,
    ForegroundCpuSet,
    BackgroundCpuSet,
    ForegroundIoPriority,
    BackgroundIoPriority,
    ForegroundMemoryPriority,
    BackgroundMemoryPriority,
    BackgroundJobObjectLimit
}
