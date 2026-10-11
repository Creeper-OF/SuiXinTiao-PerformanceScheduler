using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class ProfileListItemViewModel
{
    public required PerformanceProfile Profile { get; init; }

    public required string Name { get; init; }

    public required string Summary { get; init; }

    public required string EnabledText { get; init; }
}
