namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryAnalysisSummary
{
    public ResourcePressureSummary ResourcePressure { get; init; } = new();

    public ResourceContentionSummary ResourceContention { get; init; } = new();

    public ResourceAdvisorySummary ResourceAdvisory { get; init; } = new();

    public ResourceArbitrationSummary ResourceArbitration { get; init; } = new();

    public TargetExperienceEvaluation TargetExperience { get; init; } = new();

    public TargetExperienceControlDecision TargetExperienceControl { get; init; } = new();

    public TelemetryComparisonSummary Comparison { get; init; } = new();

    public TelemetrySchedulingComparisonSummary SchedulingComparison { get; init; } = new();

    public IReadOnlyList<TelemetryTrendSummary> Trends { get; init; } =
        Array.Empty<TelemetryTrendSummary>();
}
