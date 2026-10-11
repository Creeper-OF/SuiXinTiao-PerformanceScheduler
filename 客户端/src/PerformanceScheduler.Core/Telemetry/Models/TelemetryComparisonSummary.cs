namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryComparisonSummary
{
    public bool HasPreviousObservation { get; init; }

    public IReadOnlyList<TelemetryMetricDelta> Deltas { get; init; } =
        Array.Empty<TelemetryMetricDelta>();

    public bool HasComparableMetrics => Deltas.Count > 0;
}
