using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class CommunityProfileCatalogFilter
{
    public IReadOnlyList<CommunityProfileEntry> Apply(
        IEnumerable<CommunityProfileEntry> entries,
        CommunityProfileFilterCriteria criteria)
    {
        var query = entries;

        if (criteria.SourceFilter != CommunitySourceFilter.All)
        {
            var desiredSource = criteria.SourceFilter == CommunitySourceFilter.Official
                ? CommunityProfileSource.Official
                : CommunityProfileSource.Community;
            query = query.Where(entry => entry.Source == desiredSource);
        }

        if (criteria.PowerSourceFilter != PowerSourceMode.Any)
        {
            query = query.Where(entry => entry.Profile.PowerSourceMode == criteria.PowerSourceFilter);
        }

        if (criteria.ClassificationFilter is { } classificationFilter)
        {
            query = query.Where(entry => entry.Profile.TargetClassification == classificationFilter);
        }

        if (!string.IsNullOrWhiteSpace(criteria.SearchText))
        {
            var search = criteria.SearchText.Trim();
            query = query.Where(entry =>
                Contains(entry.Name, search) ||
                Contains(entry.Author, search) ||
                Contains(entry.Summary, search) ||
                Contains(entry.Profile.Name, search) ||
                Contains(entry.Profile.Notes, search));
        }

        return query.ToArray();
    }

    private static bool Contains(string input, string search) =>
        !string.IsNullOrWhiteSpace(input) &&
        input.Contains(search, StringComparison.OrdinalIgnoreCase);
}
