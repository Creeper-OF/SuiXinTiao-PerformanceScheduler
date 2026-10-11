namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TargetExperienceControlDecision
{
    public TargetExperienceControlState State { get; init; } = TargetExperienceControlState.Disabled;

    public bool IsStable { get; init; }

    public int EvidenceSampleCount { get; init; }

    public int StableSampleCount { get; init; }

    public TargetExperienceAdjustmentRecommendation? Recommendation { get; init; }

    public string ReasonKey { get; init; } = "TargetExperience.Disabled";
}
