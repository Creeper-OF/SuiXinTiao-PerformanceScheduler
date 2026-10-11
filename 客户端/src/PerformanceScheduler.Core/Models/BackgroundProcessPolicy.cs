namespace PerformanceScheduler.Core.Models;

public sealed record BackgroundProcessPolicy
{
    public string Category { get; init; } = "default";

    public PriorityLevel TargetPriority { get; init; } = PriorityLevel.BelowNormal;

    public bool PreferEfficiencyMode { get; init; }

    public long? TargetAffinityMask { get; init; }

    public IReadOnlyList<uint>? TargetCpuSets { get; init; }

    public IoPriorityLevel? TargetIoPriority { get; init; }

    public MemoryPriorityLevel? TargetMemoryPriority { get; init; }

    public double? TargetJobCpuRatePercent { get; init; }

    public ulong? TargetJobMemoryLimitBytes { get; init; }

    public JobObjectLimitPreference? JobObjectLimits { get; init; }

    public JobObjectLimitPreference? GetEffectiveJobObjectLimits()
    {
        if (JobObjectLimits is not null)
        {
            return JobObjectLimits;
        }

        if (TargetJobCpuRatePercent.HasValue || TargetJobMemoryLimitBytes.HasValue)
        {
            return new JobObjectLimitPreference
            {
                CpuRate = new JobObjectCpuRatePreference
                {
                    Enabled = TargetJobCpuRatePercent.HasValue,
                    MaxCpuRatePercent = TargetJobCpuRatePercent,
                    HardCap = true
                },
                MemoryLimit = new JobObjectMemoryLimitPreference
                {
                    Enabled = TargetJobMemoryLimitBytes.HasValue,
                    MaxCommitMemoryBytes = TargetJobMemoryLimitBytes
                }
            };
        }

        return null;
    }

    public string Notes { get; init; } = string.Empty;
}
