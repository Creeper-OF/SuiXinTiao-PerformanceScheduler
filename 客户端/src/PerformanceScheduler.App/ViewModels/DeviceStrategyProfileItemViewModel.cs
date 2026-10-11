using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class DeviceStrategyProfileItemViewModel
{
    public required DeviceStrategyProfileItem Item { get; init; }

    public required string Name { get; init; }

    public required string Meta { get; init; }
}
