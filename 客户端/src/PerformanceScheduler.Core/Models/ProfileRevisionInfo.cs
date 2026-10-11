namespace PerformanceScheduler.Core.Models;

public sealed record ProfileRevisionInfo
{
    public required string RevisionId { get; init; }

    public required string DisplayName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required PerformanceProfile Profile { get; init; }
}
