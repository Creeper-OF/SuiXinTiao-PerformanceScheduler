namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TargetExperienceAdjustmentRecommendation
{
    public TargetExperienceAdjustmentKind Kind { get; init; }

    public int StableSampleCount { get; init; }

    public bool RequiresCapabilityValidation { get; init; } = true;

    public bool CanApplyAutomatically { get; init; }

    public string ReasonKey { get; init; } = "TargetExperience.InsufficientEvidence";
}
