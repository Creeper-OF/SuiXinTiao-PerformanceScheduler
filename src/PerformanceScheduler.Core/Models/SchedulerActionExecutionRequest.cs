namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerActionExecutionRequest
{
    public required Guid TransactionId { get; init; }

    public required FocusedAppContext ActiveApp { get; init; }

    public required ProfileMatchResult Match { get; init; }

    public required CapabilitySnapshot Capabilities { get; init; }

    public required PowerSourceMode PowerSourceMode { get; init; }

    public PowerPlanInfo? ActivePowerPlan { get; init; }
}
