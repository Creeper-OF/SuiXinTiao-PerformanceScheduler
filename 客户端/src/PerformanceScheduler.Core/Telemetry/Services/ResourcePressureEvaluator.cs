using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Converts system-wide telemetry into a read-only pressure hint.
/// It is intentionally independent from profile application and hardware control.
/// </summary>
public sealed class ResourcePressureEvaluator
{
    public const double ModerateThresholdPercent = 75;
    public const double HighThresholdPercent = 90;

    public ResourcePressureSummary Evaluate(TelemetryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var cpuUsage = FindMaximumMetricValue(
            observation,
            TelemetryMetricKind.SystemCpuUsagePercent);
        var gpuUsage = FindMaximumMetricValue(
            observation,
            TelemetryMetricKind.SystemGpuUsagePercent,
            TelemetryMetricKind.NvidiaGpuUsagePercent);
        var highestUsage = new[] { cpuUsage, gpuUsage }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();

        return new ResourcePressureSummary
        {
            Level = cpuUsage is null && gpuUsage is null
                ? ResourcePressureLevel.Unknown
                : highestUsage >= HighThresholdPercent
                    ? ResourcePressureLevel.High
                    : highestUsage >= ModerateThresholdPercent
                        ? ResourcePressureLevel.Moderate
                        : ResourcePressureLevel.Low,
            SystemCpuUsagePercent = cpuUsage,
            SystemGpuUsagePercent = gpuUsage
        };
    }

    private static double? FindMaximumMetricValue(
        TelemetryObservation observation,
        params TelemetryMetricKind[] metricKinds)
    {
        var kindSet = metricKinds.ToHashSet();
        var values = TelemetryObservationSampleSelector.CollectedSamples(observation)
            .SelectMany(sample => sample.Metrics)
            .Where(metric => kindSet.Contains(metric.Kind))
            .Select(metric => metric.Value)
            .Where(IsValidPercentage)
            .ToArray();

        return values.Length == 0 ? null : values.Max();
    }

    private static bool IsValidPercentage(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 100;
}
