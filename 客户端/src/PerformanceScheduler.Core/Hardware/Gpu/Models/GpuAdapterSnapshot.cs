namespace PerformanceScheduler.Core.Models;

public sealed record GpuAdapterSnapshot
{
    public string Name { get; init; } = "Unknown";

    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;

    public bool SupportsVendorExtensions { get; init; }
}
