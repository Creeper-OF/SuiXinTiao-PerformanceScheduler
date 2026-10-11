namespace PerformanceScheduler.Core.Models;

public sealed record JobObjectLimitSnapshot
{
    public int ProcessId { get; init; }

    public bool HasCpuRateControl { get; init; }

    public double? CpuRatePercent { get; init; }

    public bool CpuRateHardCap { get; init; }

    public bool HasMemoryLimit { get; init; }

    public ulong? MaxCommitMemoryBytes { get; init; }
}
