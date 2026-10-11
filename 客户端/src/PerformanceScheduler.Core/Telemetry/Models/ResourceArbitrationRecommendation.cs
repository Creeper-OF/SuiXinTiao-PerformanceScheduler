using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record ResourceArbitrationRecommendation
{
    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public ProcessClassification Classification { get; init; } = ProcessClassification.Unknown;

    public double LoadScorePercent { get; init; }
}
