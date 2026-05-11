namespace PerformanceScheduler.Core.Models;

public sealed record DeviceStrategyPackage
{
    public int SchemaVersion { get; init; } = 1;

    public string PackageId { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Device Strategy";

    public string Author { get; init; } = "Unknown";

    public string Summary { get; init; } = string.Empty;

    public int Version { get; init; } = 1;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public CommunityProfileSource Source { get; init; } = CommunityProfileSource.Community;

    public DeviceFingerprint TargetDevice { get; init; } = new();

    public IReadOnlyList<DeviceStrategyProfileItem> Profiles { get; init; } = Array.Empty<DeviceStrategyProfileItem>();

    public string Notes { get; init; } = string.Empty;
}
