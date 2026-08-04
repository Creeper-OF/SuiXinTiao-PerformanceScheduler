using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryObservation
{
    public DateTimeOffset StartedAt { get; init; }

    public TimeSpan TotalDuration { get; init; }

    public required FocusedAppContext Target { get; init; }

    public IReadOnlyList<TelemetryProviderSample> ProviderSamples { get; init; } =
        Array.Empty<TelemetryProviderSample>();

    public bool HasMetrics => ProviderSamples.Any(sample => sample.Metrics.Count > 0);
}
