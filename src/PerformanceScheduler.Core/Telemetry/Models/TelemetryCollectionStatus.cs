namespace PerformanceScheduler.Core.Telemetry.Models;

public enum TelemetryCollectionStatus
{
    Collected,
    Warmup,
    RateLimited,
    BudgetExceeded,
    Unavailable,
    TimedOut,
    Failed
}
