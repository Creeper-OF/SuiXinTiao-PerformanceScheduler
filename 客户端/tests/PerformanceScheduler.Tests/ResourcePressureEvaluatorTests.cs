using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ResourcePressureEvaluatorTests
{
    private readonly ResourcePressureEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WithoutSystemUsage_ReturnsUnknown()
    {
        var summary = _evaluator.Evaluate(CreateObservation());

        Assert.Equal(ResourcePressureLevel.Unknown, summary.Level);
        Assert.False(summary.HasData);
    }

    [Fact]
    public void Evaluate_UsesHighestCpuOrGpuSignal()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.SystemCpuUsagePercent, 62),
            (TelemetryMetricKind.SystemGpuUsagePercent, 92)));

        Assert.Equal(ResourcePressureLevel.High, summary.Level);
        Assert.Equal(62, summary.SystemCpuUsagePercent);
        Assert.Equal(92, summary.SystemGpuUsagePercent);
    }

    [Theory]
    [InlineData(0, ResourcePressureLevel.Low)]
    [InlineData(74.99, ResourcePressureLevel.Low)]
    [InlineData(75, ResourcePressureLevel.Moderate)]
    [InlineData(89.99, ResourcePressureLevel.Moderate)]
    [InlineData(90, ResourcePressureLevel.High)]
    public void Evaluate_UsesDocumentedThresholds(
        double cpuUsage,
        ResourcePressureLevel expectedLevel)
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.SystemCpuUsagePercent, cpuUsage)));

        Assert.Equal(expectedLevel, summary.Level);
    }

    [Fact]
    public void Evaluate_UsesNvidiaGpuUsageWhenGenericGpuUsageIsUnavailable()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.NvidiaGpuUsagePercent, 80)));

        Assert.Equal(ResourcePressureLevel.Moderate, summary.Level);
        Assert.Equal(80, summary.SystemGpuUsagePercent);
    }

    [Fact]
    public void Evaluate_IgnoresInvalidPercentageValues()
    {
        var summary = _evaluator.Evaluate(CreateObservation(
            (TelemetryMetricKind.SystemCpuUsagePercent, double.NaN),
            (TelemetryMetricKind.SystemGpuUsagePercent, 101)));

        Assert.Equal(ResourcePressureLevel.Unknown, summary.Level);
        Assert.False(summary.HasData);
    }

    [Fact]
    public void Evaluate_IgnoresMetricsFromNonCollectedSamples()
    {
        var observation = CreateObservation(
            (TelemetryMetricKind.SystemCpuUsagePercent, 20)) with
        {
            ProviderSamples =
            [
                ..CreateObservation(
                    (TelemetryMetricKind.SystemCpuUsagePercent, 20)).ProviderSamples,
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

        Assert.Equal(ResourcePressureLevel.Low, summary.Level);
        Assert.Equal(20, summary.SystemCpuUsagePercent);
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
