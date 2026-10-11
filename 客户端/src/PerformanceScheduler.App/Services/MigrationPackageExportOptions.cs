namespace PerformanceScheduler.App.Services;

public sealed record MigrationPackageExportOptions
{
    public bool IncludeProfiles { get; init; } = true;

    public bool IncludeDeviceStrategyPackages { get; init; } = true;

    public bool IncludeSettings { get; init; } = true;
}
