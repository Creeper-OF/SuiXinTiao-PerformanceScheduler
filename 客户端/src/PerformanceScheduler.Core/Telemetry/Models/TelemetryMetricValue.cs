namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryMetricValue
{
    public required TelemetryMetricKind Kind { get; init; }

    public required double Value { get; init; }

    public string Unit { get; init; } = string.Empty;
}
