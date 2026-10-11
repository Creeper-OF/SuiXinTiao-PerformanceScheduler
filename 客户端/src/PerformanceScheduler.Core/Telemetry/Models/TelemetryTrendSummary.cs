namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryTrendSummary
{
    public required string ProviderId { get; init; }

    public required TelemetryMetricKind Kind { get; init; }

    public int SampleCount { get; init; }

    public double LatestValue { get; init; }

    public double AverageValue { get; init; }

    public double MinimumValue { get; init; }

    public double MaximumValue { get; init; }

    public double ChangeFromFirst { get; init; }

    public TelemetryTrendDirection Direction { get; init; }

    public string Unit { get; init; } = string.Empty;
}
