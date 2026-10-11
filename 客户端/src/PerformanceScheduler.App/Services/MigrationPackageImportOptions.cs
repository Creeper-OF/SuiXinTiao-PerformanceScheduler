namespace PerformanceScheduler.App.Services;

public sealed record MigrationPackageImportOptions
{
    public bool ImportProfiles { get; init; } = true;

    public bool ImportDeviceStrategyPackages { get; init; } = true;

    public bool ImportSettings { get; init; } = true;

    public bool OverwriteExistingProfiles { get; init; }
}
