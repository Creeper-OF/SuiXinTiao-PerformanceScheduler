namespace PerformanceScheduler.Core.Telemetry.Models;

public enum ResourceAdvisoryKind
{
    Unknown,
    NoImmediateAction,
    ReviewBackgroundLoad,
    ForegroundDominatesLoad
}
