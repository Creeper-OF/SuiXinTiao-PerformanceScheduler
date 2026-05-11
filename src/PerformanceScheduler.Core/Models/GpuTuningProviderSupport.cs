namespace PerformanceScheduler.Core.Models;

public sealed record GpuTuningProviderSupport
{
    public string ProviderName { get; init; } = "Unavailable";

    public bool SupportsVendorExtensions { get; init; }

    public bool SupportsApplyPipeline { get; init; }

    public bool SupportsClockLimit { get; init; }

    public bool SupportsVoltageControl { get; init; }

    public string StatusKey { get; init; } = "Capability.GpuUnavailable";
}
