namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record ResourceAdvisorySummary
{
    public ResourceAdvisoryKind Kind { get; init; }

    public ResourcePressureLevel PressureLevel { get; init; }

    public ResourceContentionLevel ContentionLevel { get; init; }
}
