namespace PerformanceScheduler.Core.Models;

public sealed record BackgroundProcessPriorityBaseline
{
    public int ProcessId { get; init; }

    public ProcessIdentitySnapshot Identity { get; init; } = new();

    public string ProcessName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public PriorityLevel PreviousPriority { get; init; } = PriorityLevel.Normal;

    public PriorityLevel TargetPriority { get; init; } = PriorityLevel.Normal;

    public ProcessEfficiencyModeState? PreviousEfficiencyMode { get; init; }

    public bool TargetEfficiencyMode { get; init; }
}
