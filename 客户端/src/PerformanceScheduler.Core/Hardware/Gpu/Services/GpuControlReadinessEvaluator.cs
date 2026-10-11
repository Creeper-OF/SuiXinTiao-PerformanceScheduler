using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Hardware.Gpu.Services;

/// <summary>
/// Separates read-only GPU observation from reversible limit readiness.
/// Voltage, curve, and vendor-specific writes are never implied by this result.
/// </summary>
public sealed class GpuControlReadinessEvaluator
{
    public GpuControlReadiness Evaluate(GpuCapabilitySnapshot gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);

        var features = gpu.SupportedFeatures.Distinct().ToArray();
        if (!gpu.HasDetectedAdapter)
        {
            return new GpuControlReadiness
            {
                Level = GpuControlReadinessLevel.NoAdapter,
                SupportsReadOnlyTelemetry = false,
                SupportsReversibleLimits = false,
                SupportedFeatures = features,
                ReasonKey = gpu.StatusKey
            };
        }

        var hasReset = features.Contains(GpuTuningFeature.ResetToDefault);
        var hasReversibleLimit = features.Any(feature => feature is
            GpuTuningFeature.PowerLimit or
            GpuTuningFeature.CoreClockLimit or
            GpuTuningFeature.MemoryClockLimit or
            GpuTuningFeature.TemperatureLimit);
        var reversibleLimitsReady = gpu.SupportsApplyPipeline && hasReset && hasReversibleLimit;

        return new GpuControlReadiness
        {
            Level = reversibleLimitsReady
                ? GpuControlReadinessLevel.ReversibleLimitReady
                : GpuControlReadinessLevel.ReadOnlyTelemetry,
            SupportsReadOnlyTelemetry = gpu.SupportsVendorExtensions || gpu.HasDetectedAdapter,
            SupportsReversibleLimits = reversibleLimitsReady,
            SupportedFeatures = features,
            ReasonKey = reversibleLimitsReady
                ? "Capability.GpuReversibleLimitReady"
                : gpu.SupportsApplyPipeline
                    ? "Capability.GpuReversibleLimitUnavailable"
                    : "Capability.GpuProviderApplyUnavailable"
        };
    }
}
