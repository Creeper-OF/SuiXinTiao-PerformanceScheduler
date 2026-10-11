namespace PerformanceScheduler.App.ViewModels;

public sealed record TelemetryTrendItemViewModel
{
    public required string MetricName { get; init; }

    public required string ProviderName { get; init; }

    public required string Summary { get; init; }

    public required string Direction { get; init; }

    public required string DirectionColor { get; init; }

    public double LatestRatio { get; init; }
}
