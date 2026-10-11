namespace PerformanceScheduler.Core.Models;

public sealed record JobObjectCpuRatePreference
{
    public bool Enabled { get; init; }

    public double? MaxCpuRatePercent { get; init; }

    public bool HardCap { get; init; } = true;
}
