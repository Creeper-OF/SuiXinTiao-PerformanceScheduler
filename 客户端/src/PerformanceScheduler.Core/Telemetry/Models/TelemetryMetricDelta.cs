namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryMetricDelta
{
    public required string ProviderId { get; init; }

    public required TelemetryMetricKind Kind { get; init; }

    public required double PreviousValue { get; init; }

    public required double CurrentValue { get; init; }

    public double Delta => CurrentValue - PreviousValue;

    public string Unit { get; init; } = string.Empty;
}
