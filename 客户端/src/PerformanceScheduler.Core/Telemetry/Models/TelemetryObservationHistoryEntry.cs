namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record TelemetryObservationHistoryEntry
{
    public long StorageId { get; init; }

    public required TelemetryObservation Observation { get; init; }

    public ResourcePressureSummary? ResourcePressure { get; init; }

    public ResourceContentionSummary? ResourceContention { get; init; }

    public ResourceAdvisorySummary? ResourceAdvisory { get; init; }
}
