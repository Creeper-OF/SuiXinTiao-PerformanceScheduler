namespace PerformanceScheduler.Core.Models;

public sealed record PowerPlanInfo(
    Guid SchemeGuid,
    string Name,
    bool IsActive);
