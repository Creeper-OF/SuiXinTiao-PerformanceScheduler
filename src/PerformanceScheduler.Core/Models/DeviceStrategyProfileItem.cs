namespace PerformanceScheduler.Core.Models;

public sealed record DeviceStrategyProfileItem
{
    public string ItemId { get; init; } = Guid.NewGuid().ToString("N");

    public DeviceStrategyProfileRole Role { get; init; } = DeviceStrategyProfileRole.AppSpecific;

    public DeviceStrategyProfileSource Source { get; init; } = DeviceStrategyProfileSource.Bundled;

    public string? SourceProfileEntryId { get; init; }

    public bool IsEnabledByDefault { get; init; } = true;

    public string Notes { get; init; } = string.Empty;

    public PerformanceProfile Profile { get; init; } = new();
}
