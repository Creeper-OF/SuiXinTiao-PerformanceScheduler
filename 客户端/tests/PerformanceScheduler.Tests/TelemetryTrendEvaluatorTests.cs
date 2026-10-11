using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TelemetryTrendEvaluatorTests
{
    private readonly TelemetryTrendEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WithSameProcessHistory_ReturnsRangeAndDirection()
    {
        var result = _evaluator.Evaluate(
            CreateObservation("game", "12:02", (TelemetryMetricKind.ProcessCpuUsagePercent, 50)),
            [
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 2,
                    Observation = CreateObservation("game", "12:01", (TelemetryMetricKind.ProcessCpuUsagePercent, 40))
                },
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 1,
                    Observation = CreateObservation("game", "12:00", (TelemetryMetricKind.ProcessCpuUsagePercent, 30))
                }
            ]);

        var summary = Assert.Single(result);
        Assert.Equal(3, summary.SampleCount);
        Assert.Equal(40, summary.AverageValue);
        Assert.Equal(30, summary.MinimumValue);
        Assert.Equal(50, summary.MaximumValue);
        Assert.Equal(20, summary.ChangeFromFirst);
        Assert.Equal(TelemetryTrendDirection.Rising, summary.Direction);
    }

    [Fact]
    public void Evaluate_WithDifferentProcessHistory_DoesNotMixObservations()
    {
        var result = _evaluator.Evaluate(
            CreateObservation("game", "12:01", (TelemetryMetricKind.ProcessCpuUsagePercent, 50)),
            [
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 1,
                    Observation = CreateObservation("browser", "12:00", (TelemetryMetricKind.ProcessCpuUsagePercent, 30))
                }
            ]);

        Assert.Empty(result);
    }

    [Fact]
    public void Evaluate_WithDifferentProviders_DoesNotPairMetrics()
    {
        var result = _evaluator.Evaluate(
            CreateObservation("game", "12:01", ("windows-system", TelemetryMetricKind.SystemCpuUsagePercent, 50)),
            [
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 1,
                    Observation = CreateObservation("game", "12:00", ("nvidia-nvml", TelemetryMetricKind.SystemCpuUsagePercent, 30))
                }
            ]);

        Assert.Empty(result);
    }

    [Fact]
    public void Evaluate_WithSmallChange_ReportsStable()
    {
        var result = _evaluator.Evaluate(
            CreateObservation("game", "12:01", (TelemetryMetricKind.ProcessCpuUsagePercent, 101)),
            [
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 1,
                    Observation = CreateObservation("game", "12:00", (TelemetryMetricKind.ProcessCpuUsagePercent, 100))
                }
            ]);

        Assert.Equal(TelemetryTrendDirection.Stable, Assert.Single(result).Direction);
    }

    [Fact]
    public void Evaluate_IgnoresMetricsFromNonCollectedSamples()
    {
        var historyObservation = CreateObservation(
            "game",
            "12:00",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 40)) with
        {
            ProviderSamples =
            [
                ..CreateObservation(
                    "game",
                    "12:00",
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 40)).ProviderSamples,
                CreateSample(
                    "failed-provider",
                    TelemetryCollectionStatus.Failed,
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 90))
            ]
        };
        var current = CreateObservation(
            "game",
            "12:01",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 50)) with
        {
            ProviderSamples =
            [
                ..CreateObservation(
                    "game",
                    "12:01",
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 50)).ProviderSamples,
                CreateSample(
                    "failed-provider",
                    TelemetryCollectionStatus.Failed,
                    (TelemetryMetricKind.ProcessCpuUsagePercent, 10))
            ]
        };

        var result = _evaluator.Evaluate(
            current,
            [
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 1,
                    Observation = historyObservation
                }
            ]);

        var summary = Assert.Single(result);
        Assert.Equal("test", summary.ProviderId);
        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(10, summary.ChangeFromFirst);
    }

    [Fact]
    public void Evaluate_WithInvalidMaximumSamples_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _evaluator.Evaluate(
            CreateObservation("game", "12:01", (TelemetryMetricKind.ProcessCpuUsagePercent, 50)),
            Array.Empty<TelemetryObservationHistoryEntry>(),
            maximumSamples: 1));
    }

    private static TelemetryObservation CreateObservation(
        string processName,
        string time,
        params (TelemetryMetricKind Kind, double Value)[] metrics) =>
        CreateObservation(processName, time, metrics.Select(metric => ("test", metric.Kind, metric.Value)).ToArray());

    private static TelemetryObservation CreateObservation(
        string processName,
        string time,
        params (string ProviderId, TelemetryMetricKind Kind, double Value)[] metrics) =>
        new()
        {
            StartedAt = DateTimeOffset.Parse($"2026-08-05T{time}:00Z"),
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
                        Unit = "%"
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
