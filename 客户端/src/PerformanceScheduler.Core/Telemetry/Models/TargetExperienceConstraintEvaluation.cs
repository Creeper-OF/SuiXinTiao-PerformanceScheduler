using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TargetExperienceConstraintEvaluation
{
    public required TelemetryMetricKind MetricKind { get; init; }

    public double? ActualValue { get; init; }

    public double? TargetValue { get; init; }

    public TargetExperienceConstraintStatus Status { get; init; }

    public string ReasonKey { get; init; } = string.Empty;
}
