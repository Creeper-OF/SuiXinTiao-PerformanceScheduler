namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetrySchedulingComparisonSummary
{
    public bool HasEligibleRun { get; init; }

    public bool HasBeforeObservation { get; init; }

    public bool HasAfterObservation { get; init; }

    public Guid? RunId { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public IReadOnlyList<TelemetryMetricDelta> Deltas { get; init; } =
        Array.Empty<TelemetryMetricDelta>();

    public bool HasComparableMetrics => Deltas.Count > 0;
}
