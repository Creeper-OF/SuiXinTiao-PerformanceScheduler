using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Compares two observations for the same foreground process without mixing applications or providers.
/// The result is descriptive and never drives an action.
/// </summary>
public sealed class TelemetryComparisonEvaluator
{
    public TelemetryComparisonSummary Compare(
        TelemetryObservation current,
        TelemetryObservation? previous)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null ||
            !string.Equals(
                current.Target.ProcessName,
                previous.Target.ProcessName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new TelemetryComparisonSummary();
        }

        var previousMetrics = SelectMetrics(previous);
        var deltas = SelectMetrics(current)
            .Where(pair => previousMetrics.ContainsKey(pair.Key))
            .Select(pair => new TelemetryMetricDelta
            {
                ProviderId = pair.Key.ProviderId,
                Kind = pair.Key.Kind,
                PreviousValue = previousMetrics[pair.Key].Value,
                CurrentValue = pair.Value.Value,
                Unit = pair.Value.Unit
            })
            .OrderBy(delta => delta.Kind)
            .ThenBy(delta => delta.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TelemetryComparisonSummary
        {
            HasPreviousObservation = true,
            Deltas = deltas
        };
    }

    private static IReadOnlyDictionary<MetricKey, (double Value, string Unit)> SelectMetrics(
        TelemetryObservation observation) =>
        TelemetryObservationSampleSelector.CollectedSamples(observation)
            .SelectMany(sample => sample.Metrics.Select(metric => new
            {
                Key = new MetricKey(sample.ProviderId, metric.Kind),
                Metric = metric
            }))
            .Where(item => double.IsFinite(item.Metric.Value))
            .GroupBy(item => item.Key)
            .ToDictionary(
                group => group.Key,
                group =>
                (
                    Value: group.First().Metric.Value,
                    Unit: group.Select(item => item.Metric.Unit)
                        .FirstOrDefault(unit => !string.IsNullOrWhiteSpace(unit)) ?? string.Empty
                ));

    private readonly record struct MetricKey(string ProviderId, TelemetryMetricKind Kind);
}
