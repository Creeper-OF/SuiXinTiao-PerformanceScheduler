namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TargetExperienceEvaluation
{
    public TargetExperienceState State { get; init; } = TargetExperienceState.Disabled;

    public bool IsConfigured { get; init; }

    public bool CanRecommendAdjustment { get; init; }

    public bool CanApplyAutomatically { get; init; }

    public IReadOnlyList<TargetExperienceConstraintEvaluation> Constraints { get; init; } =
        Array.Empty<TargetExperienceConstraintEvaluation>();

    public string ReasonKey { get; init; } = "TargetExperience.Disabled";
}
