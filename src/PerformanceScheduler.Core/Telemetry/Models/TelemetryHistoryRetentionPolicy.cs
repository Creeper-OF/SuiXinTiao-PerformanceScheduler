namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryHistoryRetentionPolicy
{
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(7);

    public int MaximumObservations { get; init; } = 5000;

    public int DeleteBatchSize { get; init; } = 500;

    public static TelemetryHistoryRetentionPolicy Default { get; } = new();

    public TelemetryHistoryRetentionPolicy Normalize() =>
        this with
        {
            MaximumAge = MaximumAge <= TimeSpan.Zero ? TimeSpan.FromDays(1) : MaximumAge,
            MaximumObservations = Math.Clamp(MaximumObservations, 100, 100_000),
            DeleteBatchSize = Math.Clamp(DeleteBatchSize, 10, 5000)
        };
}
