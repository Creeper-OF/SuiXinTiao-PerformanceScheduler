using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Compares foreground process usage with system usage to expose possible background contention.
/// This is diagnostic evidence only and never applies a scheduling action.
/// </summary>
public sealed class ResourceContentionEvaluator
{
    public const double ElevatedSystemThresholdPercent = 75;
    public const double HighSystemThresholdPercent = 90;
    public const double ElevatedGapThresholdPercent = 25;
    public const double HighGapThresholdPercent = 40;

    public ResourceContentionSummary Evaluate(TelemetryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var foregroundCpu = FindMaximumMetricValue(
            observation,
            TelemetryMetricKind.ProcessCpuUsagePercent);
        var systemCpu = FindMaximumMetricValue(
            observation,
            TelemetryMetricKind.SystemCpuUsagePercent);
        var foregroundGpu = FindMaximumMetricValue(
            observation,
            TelemetryMetricKind.ProcessGpuUsagePercent);
        var systemGpu = FindMaximumMetricValue(
            observation,
            TelemetryMetricKind.SystemGpuUsagePercent,
            TelemetryMetricKind.NvidiaGpuUsagePercent);

        var cpuLevel = EvaluatePair(foregroundCpu, systemCpu);
        var gpuLevel = EvaluatePair(foregroundGpu, systemGpu);

        return new ResourceContentionSummary
        {
            Level = MaxLevel(cpuLevel, gpuLevel),
            ForegroundCpuUsagePercent = foregroundCpu,
            SystemCpuUsagePercent = systemCpu,
            ForegroundGpuUsagePercent = foregroundGpu,
            SystemGpuUsagePercent = systemGpu
        };
    }

    private static ResourceContentionLevel EvaluatePair(double? foreground, double? system)
    {
        if (foreground is null || system is null)
        {
            return ResourceContentionLevel.Unknown;
        }

        var gap = system.Value - foreground.Value;
        if (system >= HighSystemThresholdPercent && gap >= HighGapThresholdPercent)
        {
            return ResourceContentionLevel.High;
        }

        return system >= ElevatedSystemThresholdPercent && gap >= ElevatedGapThresholdPercent
            ? ResourceContentionLevel.Elevated
            : ResourceContentionLevel.None;
    }

    private static ResourceContentionLevel MaxLevel(
        ResourceContentionLevel first,
        ResourceContentionLevel second)
    {
        if (first == ResourceContentionLevel.High || second == ResourceContentionLevel.High)
        {
            return ResourceContentionLevel.High;
        }

        if (first == ResourceContentionLevel.Elevated || second == ResourceContentionLevel.Elevated)
        {
            return ResourceContentionLevel.Elevated;
        }

        if (first == ResourceContentionLevel.None || second == ResourceContentionLevel.None)
        {
            return ResourceContentionLevel.None;
        }

        return ResourceContentionLevel.Unknown;
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
