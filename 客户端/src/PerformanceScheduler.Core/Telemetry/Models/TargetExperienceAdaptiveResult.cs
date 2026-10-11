namespace PerformanceScheduler.Core.Telemetry.Models;

/// <summary>
/// Outcome of evaluating and applying target-experience adaptive auto-tuning.
/// </summary>
public sealed record TargetExperienceAdaptiveResult
{
    public bool Applied { get; init; }

    public TargetExperienceAutoTuningPlan? Plan { get; init; }

    public int? AppliedProcessorMaxPercent { get; init; }

    public string ReasonKey { get; init; } = string.Empty;
}
