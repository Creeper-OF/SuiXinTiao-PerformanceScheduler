namespace PerformanceScheduler.Core.Models;

public sealed record CommunityApiHealth
{
    public string Status { get; init; } = string.Empty;

    public string Service { get; init; } = string.Empty;

    public string ApiVersion { get; init; } = string.Empty;

    public DateTimeOffset Utc { get; init; }
}
