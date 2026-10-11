namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryProviderSample
{
    public required string ProviderId { get; init; }

    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;

    public TelemetryCollectionStatus Status { get; init; }

    public IReadOnlyList<TelemetryMetricValue> Metrics { get; init; } =
        Array.Empty<TelemetryMetricValue>();

    public TimeSpan CollectionDuration { get; init; }

    public string ReasonCode { get; init; } = string.Empty;

    public string TechnicalMessage { get; init; } = string.Empty;
}
