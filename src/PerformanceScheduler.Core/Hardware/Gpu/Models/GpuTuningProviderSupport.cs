namespace PerformanceScheduler.Core.Models;

public sealed record GpuTuningProviderSupport
{
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
