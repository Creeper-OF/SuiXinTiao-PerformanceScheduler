using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class CommunityProfileCatalogFilterTests
{
    [Fact]
    public void Apply_FiltersBySourcePowerClassificationAndSearch()
    {
        var filter = new CommunityProfileCatalogFilter();
        var entries = new[]
        {
            new CommunityProfileEntry
            {
                EntryId = "official-game-ac",
                Name = "Official AC Game",
                Author = "Team",
                Summary = "Official gaming preset",
                Source = CommunityProfileSource.Official,
                Profile = new PerformanceProfile
                {
                    Name = "Official AC Game",
                    PowerSourceMode = PowerSourceMode.Ac,
                    TargetClassification = ProcessClassification.Game
                }
            },
            new CommunityProfileEntry
            {
                EntryId = "community-browser-battery",
                Name = "Battery Browser",
                Author = "Community",
                Summary = "Cool browser preset",
                Source = CommunityProfileSource.Community,
                Profile = new PerformanceProfile
                {
                    Name = "Battery Browser",
                    PowerSourceMode = PowerSourceMode.Battery,
                    TargetClassification = ProcessClassification.Browser
                }
            }
        };

        var results = filter.Apply(entries, new CommunityProfileFilterCriteria
        {
            SourceFilter = CommunitySourceFilter.Community,
            PowerSourceFilter = PowerSourceMode.Battery,
            ClassificationFilter = ProcessClassification.Browser,
            SearchText = "cool"
        });

        var result = Assert.Single(results);
        Assert.Equal("community-browser-battery", result.EntryId);
    }

    [Fact]
    public void Apply_UsesAllFiltersAsPassThroughWhenUnset()
    {
        var filter = new CommunityProfileCatalogFilter();
        var entries = new[]
        {
            new CommunityProfileEntry { EntryId = "one", Name = "One" },
            new CommunityProfileEntry { EntryId = "two", Name = "Two" }
        };

        var results = filter.Apply(entries, new CommunityProfileFilterCriteria());

        Assert.Equal(2, results.Count);
    }
}
