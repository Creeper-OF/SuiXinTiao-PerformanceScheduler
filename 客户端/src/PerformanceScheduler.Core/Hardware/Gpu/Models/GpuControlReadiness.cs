namespace PerformanceScheduler.Core.Models;

public sealed record GpuControlReadiness
{
    public GpuControlReadinessLevel Level { get; init; } = GpuControlReadinessLevel.NoAdapter;

    public bool SupportsReadOnlyTelemetry { get; init; }

    public bool SupportsReversibleLimits { get; init; }

    public IReadOnlyList<GpuTuningFeature> SupportedFeatures { get; init; } = Array.Empty<GpuTuningFeature>();

    public string ReasonKey { get; init; } = "Capability.GpuUnavailable";
}
