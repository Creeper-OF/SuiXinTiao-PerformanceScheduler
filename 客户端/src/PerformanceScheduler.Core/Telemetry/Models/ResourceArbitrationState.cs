namespace PerformanceScheduler.Core.Telemetry.Models;

public enum ResourceArbitrationState
{
    Disabled,
    NoContention,
    InsufficientEvidence,
    ProtectedOnly,
    RecommendBackgroundRelief
}
