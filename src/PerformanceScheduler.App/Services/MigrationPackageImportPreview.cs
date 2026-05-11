using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.Services;

public sealed record MigrationPackageImportPreview
{
    public DeviceFingerprint SourceDevice { get; init; } = new();

    public DeviceCompatibilityResult Compatibility { get; init; } = new();

    public int ProfileCount { get; init; }

    public int DeviceStrategyPackageCount { get; init; }

    public bool HasSettings { get; init; }
}
