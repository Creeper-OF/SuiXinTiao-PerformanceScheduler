namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryCollectionBudget
{
    public TimeSpan MinimumSamplingInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumProviderDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaximumObservationDuration { get; init; } = TimeSpan.FromMilliseconds(500);

    public static TelemetryCollectionBudget Default { get; } = new();
}
