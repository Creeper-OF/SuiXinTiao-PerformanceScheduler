using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class SchedulerRunHistoryItemViewModel
{
    public required SchedulerRunHistoryEntry Entry { get; init; }

    public string DisplayName { get; init; } = string.Empty;
}
