namespace PerformanceScheduler.Core.Models;

public sealed record CommunityProfileEntry
{
    public int SchemaVersion { get; init; } = 1;

    public string EntryId { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Community Profile";

    public string Author { get; init; } = "Unknown";

    public string Summary { get; init; } = string.Empty;

    public CommunityProfileSource Source { get; init; } = CommunityProfileSource.Community;

    public int Downloads { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public PerformanceProfile Profile { get; init; } = new();
}
