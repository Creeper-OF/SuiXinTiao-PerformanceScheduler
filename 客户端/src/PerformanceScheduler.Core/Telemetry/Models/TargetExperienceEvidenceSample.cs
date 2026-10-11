namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TargetExperienceEvidenceSample
{
    public required DateTimeOffset ObservedAt { get; init; }

    public required TargetExperienceEvaluation Evaluation { get; init; }
}
