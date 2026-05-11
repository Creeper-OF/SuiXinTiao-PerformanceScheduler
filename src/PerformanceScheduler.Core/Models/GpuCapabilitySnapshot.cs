namespace PerformanceScheduler.Core.Models;

public sealed record GpuCapabilitySnapshot
{
    public bool HasDetectedAdapter { get; init; }

    public string AdapterName { get; init; } = "Unknown";

    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;

    public string ProviderName { get; init; } = "Unavailable";

    public bool SupportsVendorExtensions { get; init; }

    public bool SupportsApplyPipeline { get; init; }

    public bool SupportsClockLimit { get; init; }

    public bool SupportsVoltageControl { get; init; }

    public string StatusKey { get; init; } = "Capability.GpuUnavailable";
}
