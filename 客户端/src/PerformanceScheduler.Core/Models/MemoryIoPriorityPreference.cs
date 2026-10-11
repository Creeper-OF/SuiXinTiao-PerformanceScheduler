namespace PerformanceScheduler.Core.Models;

public sealed record MemoryIoPriorityPreference
{
    public bool Enabled { get; init; }

    public IoPriorityLevel ForegroundIoPriority { get; init; } = IoPriorityLevel.Normal;

    public IoPriorityLevel BackgroundIoPriority { get; init; } = IoPriorityLevel.VeryLow;

    public MemoryPriorityLevel ForegroundMemoryPriority { get; init; } = MemoryPriorityLevel.Normal;

    public MemoryPriorityLevel BackgroundMemoryPriority { get; init; } = MemoryPriorityLevel.VeryLow;
}
