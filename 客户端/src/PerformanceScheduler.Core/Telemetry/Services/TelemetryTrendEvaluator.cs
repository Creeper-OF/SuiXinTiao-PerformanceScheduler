using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Builds a small, same-process trend summary from recent observations.
/// The result is descriptive and never drives an action.
/// </summary>
public sealed class TelemetryTrendEvaluator
{
    private const double RelativeStabilityThreshold = 0.02;

    public IReadOnlyList<TelemetryTrendSummary> Evaluate(
        TelemetryObservation current,
        IEnumerable<TelemetryObservationHistoryEntry> history,
        int maximumSamples = 5)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);

        if (maximumSamples < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSamples), "At least two observations are required for a trend.");
        }

        var observations = history
            .Where(entry => string.Equals(
                entry.Observation.Target.ProcessName,
                current.Target.ProcessName,
                StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Observation.StartedAt < current.StartedAt)
            .Select(entry => entry.Observation)
            .Append(current)
            .OrderBy(observation => observation.StartedAt)
            .TakeLast(maximumSamples)
            .ToArray();

        if (observations.Length < 2)
        {
            return Array.Empty<TelemetryTrendSummary>();
        }

        var series = observations
            .Select(SelectMetrics)
            .SelectMany(metrics => metrics.Keys)
            .Distinct()
            .Select(key => new
            {
                Key = key,
                Values = observations
                    .Select(SelectMetrics)
                    .Where(metrics => metrics.TryGetValue(key, out _))
                    .Select(metrics => metrics[key])
                    .ToArray()
            })
            .Where(series => series.Values.Length >= 2)
            .Select(series => CreateSummary(series.Key, series.Values))
            .OrderBy(summary => summary.Kind)
            .ThenBy(summary => summary.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return series;
    }

    private static TelemetryTrendSummary CreateSummary(
        MetricKey key,
        IReadOnlyList<MetricPoint> points)
    {
        var first = points[0].Value;
        var latest = points[^1].Value;
        var change = latest - first;
        var scale = Math.Max(Math.Abs(first), Math.Abs(latest));
        var threshold = Math.Max(0.01, scale * RelativeStabilityThreshold);
        var direction = Math.Abs(change) <= threshold
            ? TelemetryTrendDirection.Stable
            : change > 0
                ? TelemetryTrendDirection.Rising
                : TelemetryTrendDirection.Falling;

        return new TelemetryTrendSummary
        {
            ProviderId = key.ProviderId,
            Kind = key.Kind,
            SampleCount = points.Count,
            LatestValue = latest,
            AverageValue = points.Average(point => point.Value),
            MinimumValue = points.Min(point => point.Value),
            MaximumValue = points.Max(point => point.Value),
            ChangeFromFirst = change,
            Direction = direction,
            Unit = points.Select(point => point.Unit)
                .FirstOrDefault(unit => !string.IsNullOrWhiteSpace(unit)) ?? string.Empty
        };
    }

    private static IReadOnlyDictionary<MetricKey, MetricPoint> SelectMetrics(
        TelemetryObservation observation)
    {
        var selected = new Dictionary<MetricKey, MetricPoint>();
        foreach (var sample in TelemetryObservationSampleSelector.CollectedSamples(observation))
        {
            foreach (var metric in sample.Metrics)
            {
                if (!double.IsFinite(metric.Value))
                {
                    continue;
                }

                var key = new MetricKey(sample.ProviderId, metric.Kind);
                if (!selected.ContainsKey(key))
                {
                    selected[key] = new MetricPoint(metric.Value, metric.Unit);
                }
            }
        }

        return selected;
    }

    private readonly record struct MetricKey(string ProviderId, TelemetryMetricKind Kind);

    private readonly record struct MetricPoint(double Value, string Unit);
}
