using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TelemetryComparisonEvaluatorTests
{
    private readonly TelemetryComparisonEvaluator _evaluator = new();

    [Fact]
    public void Compare_WithDifferentProcessNames_DoesNotMixObservations()
    {
        var result = _evaluator.Compare(
            CreateObservation("game", (TelemetryMetricKind.ProcessCpuUsagePercent, 50)),
            CreateObservation("browser", (TelemetryMetricKind.ProcessCpuUsagePercent, 10)));

        Assert.False(result.HasPreviousObservation);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void Compare_WithSameProcess_ReturnsCurrentMinusPrevious()
    {
        var result = _evaluator.Compare(
            CreateObservation(
                "game",
                (TelemetryMetricKind.ProcessCpuUsagePercent, 50),
                (TelemetryMetricKind.FramesPerSecondAverage, 120)),
            CreateObservation(
                "game",
                (TelemetryMetricKind.ProcessCpuUsagePercent, 30),
                (TelemetryMetricKind.FramesPerSecondAverage, 100)));

        Assert.True(result.HasPreviousObservation);
        Assert.Equal(2, result.Deltas.Count);
        Assert.Equal(20, Assert.Single(result.Deltas, delta =>
            delta.Kind == TelemetryMetricKind.ProcessCpuUsagePercent).Delta);
        Assert.Equal(20, Assert.Single(result.Deltas, delta =>
            delta.Kind == TelemetryMetricKind.FramesPerSecondAverage).Delta);
    }

    [Fact]
    public void Compare_WithDuplicateMetricsFromDifferentProviders_DoesNotMixValues()
    {
        var result = _evaluator.Compare(
            CreateObservation(
                "game",
                ("windows-system", TelemetryMetricKind.SystemCpuUsagePercent, 60),
                ("nvidia-nvml", TelemetryMetricKind.SystemCpuUsagePercent, 80)),
            CreateObservation(
                "game",
                ("windows-system", TelemetryMetricKind.SystemCpuUsagePercent, 50),
                ("nvidia-nvml", TelemetryMetricKind.SystemCpuUsagePercent, 70)));

        Assert.Equal(2, result.Deltas.Count);
        Assert.Equal(10, Assert.Single(result.Deltas, delta => delta.ProviderId == "windows-system").Delta);
        Assert.Equal(10, Assert.Single(result.Deltas, delta => delta.ProviderId == "nvidia-nvml").Delta);
    }

    [Fact]
    public void Compare_WithSameMetricButDifferentProvider_DoesNotCompare()
    {
        var result = _evaluator.Compare(
            CreateObservation(
                "game",
                ("windows-system", TelemetryMetricKind.SystemCpuUsagePercent, 60)),
            CreateObservation(
                "game",
                ("nvidia-nvml", TelemetryMetricKind.SystemCpuUsagePercent, 50)));

        Assert.True(result.HasPreviousObservation);
        Assert.False(result.HasComparableMetrics);
    }

    [Fact]
    public void Compare_WithNoSharedMetrics_ReportsPreviousButNoComparison()
    {
        var result = _evaluator.Compare(
            CreateObservation("game", (TelemetryMetricKind.ProcessCpuUsagePercent, 50)),
            CreateObservation("game", (TelemetryMetricKind.SystemCpuUsagePercent, 50)));

        Assert.True(result.HasPreviousObservation);
        Assert.False(result.HasComparableMetrics);
    }

    [Fact]
    public void Compare_IgnoresNonFiniteMetricValues()
    {
        var result = _evaluator.Compare(
            CreateObservation("game", (TelemetryMetricKind.ProcessCpuUsagePercent, double.NaN)),
            CreateObservation("game", (TelemetryMetricKind.ProcessCpuUsagePercent, 50)));

        Assert.True(result.HasPreviousObservation);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void Compare_IgnoresMetricsFromNonCollectedSamples()
    {
        var current = CreateObservation(
            "game",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 50)) with
        {
            ProviderSamples =
            [
                ..CreateObservation(
                    "game",
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 50)).ProviderSamples,
                CreateSample(
                    "failed-provider",
                    TelemetryCollectionStatus.Failed,
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 90))
            ]
        };
        var previous = CreateObservation(
            "game",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 40)) with
        {
            ProviderSamples =
            [
                ..CreateObservation(
                    "game",
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 40)).ProviderSamples,
                CreateSample(
                    "failed-provider",
                    TelemetryCollectionStatus.Failed,
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 10))
            ]
        };

        var result = _evaluator.Compare(current, previous);

        Assert.Equal(10, Assert.Single(result.Deltas).Delta);
        Assert.Equal("test", Assert.Single(result.Deltas).ProviderId);
    }

    private static TelemetryObservation CreateObservation(
        string processName,
        params (TelemetryMetricKind Kind, double Value)[] metrics) =>
        CreateObservation(processName, metrics.Select(metric => ("test", metric.Kind, metric.Value)).ToArray());

    private static TelemetryObservation CreateObservation(
        string processName,
        params (string ProviderId, TelemetryMetricKind Kind, double Value)[] metrics) =>
        new()
        {
            StartedAt = DateTimeOffset.UtcNow,
            Target = new FocusedAppContext
            {
                ProcessId = 42,
                ProcessName = processName,
                WindowTitle = processName,
                WindowHandle = (nint)1
            },
            ProviderSamples = metrics
                .GroupBy(metric => metric.ProviderId)
                .Select(group => new TelemetryProviderSample
                {
                    ProviderId = group.Key,
                    Status = TelemetryCollectionStatus.Collected,
                    Metrics = group.Select(metric => new TelemetryMetricValue
                    {
                        Kind = metric.Kind,
                        Value = metric.Value,
                        Unit = metric.Kind == TelemetryMetricKind.FramesPerSecondAverage ? "FPS" : "%"
                    }).ToArray()
                })
                .ToArray()
        };

    private static TelemetryProviderSample CreateSample(
        string providerId,
        TelemetryCollectionStatus status,
        params (TelemetryMetricKind Kind, double Value)[] metrics) =>
        new()
        {
            ProviderId = providerId,
            Status = status,
            Metrics = metrics.Select(metric => new TelemetryMetricValue
            {
                Kind = metric.Kind,
                Value = metric.Value,
                Unit = "%"
            }).ToArray()
        };
}
