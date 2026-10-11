namespace PerformanceScheduler.Core.Models;

public sealed record GpuPreference
{
    public GpuPreferenceMode Mode { get; init; } = GpuPreferenceMode.DriverDefault;

    public int? MaxCoreClockMHz { get; init; }

    public int? VoltageOffsetMv { get; init; }

    public bool AllowVendorApi { get; init; }
}
