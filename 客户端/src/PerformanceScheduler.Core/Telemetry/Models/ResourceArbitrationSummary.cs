namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record ResourceArbitrationSummary
{
    public ResourceArbitrationState State { get; init; } = ResourceArbitrationState.InsufficientEvidence;

    public ResourceContentionLevel ContentionLevel { get; init; } = ResourceContentionLevel.Unknown;

    public int CandidateCount { get; init; }

    public int ProtectedCandidateCount { get; init; }

    public int MeasuredCandidateCount { get; init; }

    public IReadOnlyList<ResourceArbitrationRecommendation> Recommendations { get; init; } =
        Array.Empty<ResourceArbitrationRecommendation>();

    public string ReasonKey { get; init; } = "ResourceArbitration.InsufficientEvidence";
}
