namespace PerformanceScheduler.Core.Telemetry.Models;

/// <summary>
/// A concrete, bounded auto-tuning plan generated from stable target-experience evidence.
/// </summary>
public sealed record TargetExperienceAutoTuningPlan
{
    public required TargetExperienceAdjustmentKind Kind { get; init; }

    public required int CurrentProcessorMaxPercent { get; init; }

    public required int TargetProcessorMaxPercent { get; init; }

    public required string ReasonKey { get; init; }

    public bool IsBoundaryReached { get; init; }

    public bool IsInCooldown { get; init; }

    public bool HasChange => TargetProcessorMaxPercent != CurrentProcessorMaxPercent;
}
