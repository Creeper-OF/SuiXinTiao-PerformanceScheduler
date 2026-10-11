using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ResourceAdvisoryEvaluatorTests
{
    private readonly ResourceAdvisoryEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WithoutEvidence_ReturnsUnknown()
    {
        var result = _evaluator.Evaluate(
            new ResourcePressureSummary(),
            new ResourceContentionSummary());

        Assert.Equal(ResourceAdvisoryKind.Unknown, result.Kind);
    }

    [Fact]
    public void Evaluate_WithElevatedContention_RecommendsReviewingBackgroundLoad()
    {
        var result = _evaluator.Evaluate(
            new ResourcePressureSummary
            {
                Level = ResourcePressureLevel.Moderate,
                SystemCpuUsagePercent = 82
            },
            new ResourceContentionSummary
            {
                Level = ResourceContentionLevel.Elevated,
                ForegroundCpuUsagePercent = 35,
                SystemCpuUsagePercent = 82
            });

        Assert.Equal(ResourceAdvisoryKind.ReviewBackgroundLoad, result.Kind);
    }

    [Fact]
    public void Evaluate_WithHighPressureButNoContention_IdentifiesForegroundLoad()
    {
        var result = _evaluator.Evaluate(
            new ResourcePressureSummary
            {
                Level = ResourcePressureLevel.High,
                SystemCpuUsagePercent = 94
            },
            new ResourceContentionSummary
            {
                Level = ResourceContentionLevel.None,
                ForegroundCpuUsagePercent = 86,
                SystemCpuUsagePercent = 94
            });

        Assert.Equal(ResourceAdvisoryKind.ForegroundDominatesLoad, result.Kind);
    }

    [Fact]
    public void Evaluate_WithLowPressure_ReturnsNoImmediateAction()
    {
        var result = _evaluator.Evaluate(
            new ResourcePressureSummary
            {
                Level = ResourcePressureLevel.Low,
                SystemCpuUsagePercent = 31
            },
            new ResourceContentionSummary
            {
                Level = ResourceContentionLevel.Unknown
            });

        Assert.Equal(ResourceAdvisoryKind.NoImmediateAction, result.Kind);
    }
}
