namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryProviderCapabilities
{
    public required string ProviderId { get; init; }

    public bool IsSupported { get; init; }

    public IReadOnlyList<TelemetryMetricKind> SupportedMetrics { get; init; } =
        Array.Empty<TelemetryMetricKind>();

    public TimeSpan MinimumSamplingInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TelemetryOverheadClass EstimatedOverhead { get; init; } = TelemetryOverheadClass.Low;

    public string UnavailableReasonCode { get; init; } = string.Empty;
}
