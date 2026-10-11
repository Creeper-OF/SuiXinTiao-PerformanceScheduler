using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerActionExecutionRequest
{
    public required Guid TransactionId { get; init; }

    public required FocusedAppContext ActiveApp { get; init; }

    public required ProfileMatchResult Match { get; init; }

    public required CapabilitySnapshot Capabilities { get; init; }

    public required PowerSourceMode PowerSourceMode { get; init; }

    public PowerPlanInfo? ActivePowerPlan { get; init; }

    /// <summary>
    /// Enables the opt-in, evidence-backed background arbitration gate.
    /// The default preserves the existing Profile background policy behavior.
    /// </summary>
    public bool UseResourceArbitration { get; init; }

    public ResourceArbitrationSummary? ResourceArbitration { get; init; }
}
