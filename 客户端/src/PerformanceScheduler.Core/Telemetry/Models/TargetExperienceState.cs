namespace PerformanceScheduler.Core.Telemetry.Models;

public enum TargetExperienceState
{
    Disabled,
    InsufficientEvidence,
    OnTarget,
    NeedsMorePerformance,
    NeedsCoolingOrPower
}

public enum TargetExperienceConstraintStatus
{
    NotConfigured,
    Unknown,
    WithinTarget,
    BelowTarget,
    AboveLimit
}
