using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Abstractions;

public interface ITelemetryHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task RecordObservationAsync(
        TelemetryObservation observation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelemetryObservationHistoryEntry>> LoadRecentAsync(
        int count = 100,
        CancellationToken cancellationToken = default);

    Task<int> PruneAsync(
        TelemetryHistoryRetentionPolicy policy,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);
}
