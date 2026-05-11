using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class CommunityProfileListItemViewModel
{
    public required CommunityProfileEntry Entry { get; init; }

    public required string Name { get; init; }

    public required string Summary { get; init; }

    public required string Description { get; init; }

    public required string Meta { get; init; }
}
