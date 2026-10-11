using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ResourceContentionEvaluatorTests
{
    private readonly ResourceContentionEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WhenSystemIsHighButForegroundOwnsTheLoad_ReturnsNone()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.ProcessCpuUsagePercent, 86),
            (TelemetryMetricKind.SystemCpuUsagePercent, 94)));

        Assert.Equal(ResourceContentionLevel.None, summary.Level);
    }

    [Fact]
    public void Evaluate_WhenBackgroundLoadLeavesLargeCpuGap_ReturnsHigh()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.ProcessCpuUsagePercent, 35),
            (TelemetryMetricKind.SystemCpuUsagePercent, 94)));

        Assert.Equal(ResourceContentionLevel.High, summary.Level);
        Assert.Equal(35, summary.ForegroundCpuUsagePercent);
        Assert.Equal(94, summary.SystemCpuUsagePercent);
    }

    [Fact]
    public void Evaluate_UsesGpuGapWhenCpuComparisonIsUnavailable()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.ProcessGpuUsagePercent, 30),
            (TelemetryMetricKind.NvidiaGpuUsagePercent, 80)));

        Assert.Equal(ResourceContentionLevel.Elevated, summary.Level);
        Assert.True(summary.HasComparison);
    }

    [Fact]
    public void Evaluate_WhenNoCompletePairExists_ReturnsUnknown()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.ProcessCpuUsagePercent, 20)));

        Assert.Equal(ResourceContentionLevel.Unknown, summary.Level);
        Assert.False(summary.HasComparison);
    }

    [Fact]
    public void Evaluate_IgnoresMetricsFromNonCollectedSamples()
    {
        var observation = CreateObservation(
            (TelemetryMetricKind.ProcessCpuUsagePercent, 70),
            (TelemetryMetricKind.SystemCpuUsagePercent, 80)) with
        {
            ProviderSamples =
            [
                ..CreateObservation(
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 70),
                    (TelemetryMetricKind.SystemCpuUsagePercent, 80)).ProviderSamples,
                new TelemetryProviderSample
                {
                    ProviderId = "failed-provider",
                    Status = TelemetryCollectionStatus.Failed,
                    Metrics =
                    [
                        new TelemetryMetricValue
                        {
                            Kind = TelemetryMetricKind.SystemCpuUsagePercent,
                            Value = 99,
                            Unit = "%"
                        }
                    ]
                }
            ]
        };

        var summary = _evaluator.Evaluate(observation);

        Assert.Equal(ResourceContentionLevel.None, summary.Level);
        Assert.Equal(80, summary.SystemCpuUsagePercent);
    }

    [Theory]
    [InlineData(75, 50, ResourceContentionLevel.Elevated)]
    [InlineData(75, 51, ResourceContentionLevel.None)]
    [InlineData(90, 50, ResourceContentionLevel.High)]
    [InlineData(90, 51, ResourceContentionLevel.Elevated)]
    public void Evaluate_UsesSystemAndGapThresholds(
        double systemUsage,
        double foregroundUsage,
        ResourceContentionLevel expectedLevel)
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.ProcessCpuUsagePercent, foregroundUsage),
            (TelemetryMetricKind.SystemCpuUsagePercent, systemUsage)));

        Assert.Equal(expectedLevel, summary.Level);
    }

    private static TelemetryObservation CreateObservation(
        params (TelemetryMetricKind Kind, double Value)[] metrics) =>
        new()
        {
            StartedAt = DateTimeOffset.UtcNow,
            Target = new FocusedAppContext
            {
                ProcessId = 42,
                ProcessName = "sample",
                WindowTitle = "Sample",
                WindowHandle = (nint)1
            },
            ProviderSamples =
            [
                new TelemetryProviderSample
                {
                    ProviderId = "test",
                    Status = TelemetryCollectionStatus.Collected,
                    Metrics = metrics.Select(metric => new TelemetryMetricValue
                    {
                        Kind = metric.Kind,
                        Value = metric.Value,
                        Unit = "%"
                    }).ToArray()
                }
            ]
        };
}
