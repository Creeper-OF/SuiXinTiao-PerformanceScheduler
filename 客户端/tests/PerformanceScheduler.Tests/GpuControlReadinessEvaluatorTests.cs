using PerformanceScheduler.Core.Hardware.Gpu.Services;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Tests;

public sealed class GpuControlReadinessEvaluatorTests
{
    private readonly GpuControlReadinessEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WhenNoAdapter_ReturnsNoAdapter()
    {
        var result = _evaluator.Evaluate(new GpuCapabilitySnapshot());

        Assert.Equal(GpuControlReadinessLevel.NoAdapter, result.Level);
        Assert.False(result.SupportsReadOnlyTelemetry);
        Assert.False(result.SupportsReversibleLimits);
    }

    [Fact]
    public void Evaluate_WhenProviderIsReadOnly_DoesNotPretendLimitsAreAvailable()
    {
        var result = _evaluator.Evaluate(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = false,
            SupportedFeatures = [GpuTuningFeature.ResetToDefault, GpuTuningFeature.CoreClockLimit]
        });

        Assert.Equal(GpuControlReadinessLevel.ReadOnlyTelemetry, result.Level);
        Assert.True(result.SupportsReadOnlyTelemetry);
        Assert.False(result.SupportsReversibleLimits);
        Assert.Equal("Capability.GpuProviderApplyUnavailable", result.ReasonKey);
    }

    [Fact]
    public void Evaluate_WhenProviderCanResetAndLimit_IsReversibleLimitReady()
    {
        var result = _evaluator.Evaluate(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = true,
            SupportedFeatures = [GpuTuningFeature.ResetToDefault, GpuTuningFeature.PowerLimit]
        });

        Assert.Equal(GpuControlReadinessLevel.ReversibleLimitReady, result.Level);
        Assert.True(result.SupportsReversibleLimits);
        Assert.Equal("Capability.GpuReversibleLimitReady", result.ReasonKey);
    }

    [Fact]
    public void Evaluate_WhenApplyPipelineLacksReset_RemainsReadOnly()
    {
        var result = _evaluator.Evaluate(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = true,
            SupportedFeatures = [GpuTuningFeature.PowerLimit]
        });

        Assert.Equal(GpuControlReadinessLevel.ReadOnlyTelemetry, result.Level);
        Assert.False(result.SupportsReversibleLimits);
        Assert.Equal("Capability.GpuReversibleLimitUnavailable", result.ReasonKey);
    }
}
