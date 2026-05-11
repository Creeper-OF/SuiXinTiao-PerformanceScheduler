using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.Services;

public sealed record MigrationPackage
{
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    public DeviceFingerprint SourceDevice { get; init; } = new();

    public IReadOnlyList<PerformanceProfile> Profiles { get; init; } = Array.Empty<PerformanceProfile>();

    public IReadOnlyList<DeviceStrategyPackage> DeviceStrategyPackages { get; init; } = Array.Empty<DeviceStrategyPackage>();

    public AppSettings? Settings { get; init; }
}
