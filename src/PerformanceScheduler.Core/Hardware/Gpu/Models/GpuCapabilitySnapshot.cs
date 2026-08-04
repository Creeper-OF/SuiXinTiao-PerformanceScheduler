namespace PerformanceScheduler.Core.Models;

public sealed record GpuCapabilitySnapshot
{
    public bool HasDetectedAdapter { get; init; }

    public string AdapterName { get; init; } = "Unknown";

    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;

    public string ProviderName { get; init; } = "Unavailable";

    public bool SupportsVendorExtensions { get; init; }

    public bool SupportsApplyPipeline { get; init; }

    public IReadOnlyList<GpuTuningFeature> SupportedFeatures { get; init; } = Array.Empty<GpuTuningFeature>();

    public bool SupportsClockLimit =>
        SupportedFeatures.Contains(GpuTuningFeature.CoreClockLimit);

    public bool SupportsVoltageControl =>
        SupportedFeatures.Contains(GpuTuningFeature.VoltageOffset) ||
        SupportedFeatures.Contains(GpuTuningFeature.VoltageCurve);

    public string StatusKey { get; init; } = "Capability.GpuUnavailable";
}
