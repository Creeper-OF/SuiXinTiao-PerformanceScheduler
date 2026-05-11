using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.Services;

public sealed record MigrationPackageImportResult
{
    public int ImportedProfiles { get; init; }

    public int ImportedDeviceStrategyPackages { get; init; }

    public bool ImportedSettings { get; init; }

    public DeviceCompatibilityResult Compatibility { get; init; } = new();
}
