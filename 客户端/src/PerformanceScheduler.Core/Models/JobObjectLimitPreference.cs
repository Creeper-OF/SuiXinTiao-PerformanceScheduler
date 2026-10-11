namespace PerformanceScheduler.Core.Models;

public sealed record JobObjectLimitPreference
{
    public JobObjectCpuRatePreference CpuRate { get; init; } = new();

    public JobObjectMemoryLimitPreference MemoryLimit { get; init; } = new();
}
