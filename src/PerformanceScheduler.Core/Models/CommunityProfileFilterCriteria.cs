namespace PerformanceScheduler.Core.Models;

public sealed record CommunityProfileFilterCriteria
{
    public string SearchText { get; init; } = string.Empty;

    public CommunitySourceFilter SourceFilter { get; init; } = CommunitySourceFilter.All;

    public PowerSourceMode PowerSourceFilter { get; init; } = PowerSourceMode.Any;

    public ProcessClassification? ClassificationFilter { get; init; }
}
