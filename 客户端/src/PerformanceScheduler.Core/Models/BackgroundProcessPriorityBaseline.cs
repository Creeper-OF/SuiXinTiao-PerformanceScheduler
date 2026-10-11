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

    public long? TargetAffinityMask { get; init; }

    public IReadOnlyList<uint>? TargetCpuSets { get; init; }

    public IoPriorityLevel? TargetIoPriority { get; init; }

    public MemoryPriorityLevel? TargetMemoryPriority { get; init; }

    public JobObjectLimitPreference? TargetJobLimits { get; init; }
}
