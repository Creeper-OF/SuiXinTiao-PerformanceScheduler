using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class DeviceStrategyPackageListItemViewModel
{
    public required DeviceStrategyPackage Package { get; init; }

    public required string Name { get; init; }

    public required string Summary { get; init; }

    public required string Meta { get; init; }
}
