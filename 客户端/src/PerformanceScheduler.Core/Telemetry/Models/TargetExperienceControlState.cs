namespace PerformanceScheduler.Core.Telemetry.Models;

public enum TargetExperienceControlState
{
    Disabled,
    InsufficientEvidence,
    AwaitingStableEvidence,
    OnTarget,
    NeedsMorePerformance,
    NeedsCoolingOrPower
}
