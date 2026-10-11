using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TelemetryAnalysisEvaluatorTests
{
    private readonly TelemetryAnalysisEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_UsesNewestMatchingObservationForComparison()
    {
        var current = CreateObservation(
            "game",
            "12:03",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 50));
        var newest = CreateObservation(
            "game",
            "12:02",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 30));
        var oldest = CreateObservation(
            "game",
            "12:01",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 10));

        var result = _evaluator.Evaluate(
            current,
            [
                new TelemetryObservationHistoryEntry { StorageId = 1, Observation = oldest },
                new TelemetryObservationHistoryEntry { StorageId = 2, Observation = newest }
            ]);

        Assert.Equal(20, Assert.Single(result.Comparison.Deltas).Delta);
        Assert.Equal(ResourcePressureLevel.Unknown, result.ResourcePressure.Level);
        Assert.Equal(3, Assert.Single(result.Trends).SampleCount);
    }

    [Fact]
    public void Evaluate_CombinesResourceEvidenceAndSchedulingComparison()
    {
        var current = CreateObservation(
            "game",
            "12:04",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 35),
            (TelemetryMetricKind.SystemCpuUsagePercent, 94));
        var before = CreateObservation(
            "game",
            "12:01",
            (TelemetryMetricKind.ProcessCpuUsagePercent, 35));

        var result = _evaluator.Evaluate(
            current,
            [new TelemetryObservationHistoryEntry { StorageId = 1, Observation = before }],
            new SchedulerRunHistoryEntry
            {
                RunId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.Parse("2026-08-05T12:02:00Z"),
                ProcessName = "game",
                ProfileName = "Game High Performance",
                Success = true
            });

        Assert.Equal(ResourcePressureLevel.High, result.ResourcePressure.Level);
        Assert.Equal(ResourceContentionLevel.High, result.ResourceContention.Level);
        Assert.Equal(ResourceAdvisoryKind.ReviewBackgroundLoad, result.ResourceAdvisory.Kind);
        Assert.True(result.SchedulingComparison.HasEligibleRun);
        Assert.True(result.SchedulingComparison.HasBeforeObservation);
        Assert.True(result.SchedulingComparison.HasAfterObservation);
        Assert.Equal(0, Assert.Single(result.SchedulingComparison.Deltas).Delta);
    }

    [Fact]
    public void Evaluate_IgnoresFutureObservationsForPreviousAndTrend()
    {
        var result = _evaluator.Evaluate(
            CreateObservation(
                "game",
                "12:03",
                (TelemetryMetricKind.ProcessCpuUsagePercent, 50)),
            [
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 3,
                    Observation = CreateObservation(
                        "game",
                        "12:04",
                        (TelemetryMetricKind.ProcessCpuUsagePercent, 90))
                },
                new TelemetryObservationHistoryEntry
                {
                    StorageId = 1,
                    Observation = CreateObservation(
                        "game",
                        "12:02",
                        (TelemetryMetricKind.ProcessCpuUsagePercent, 30))
                }
            ]);

        Assert.Equal(20, Assert.Single(result.Comparison.Deltas).Delta);
        var trend = Assert.Single(result.Trends);
        Assert.Equal(2, trend.SampleCount);
        Assert.Equal(50, trend.MaximumValue);
    }

    private static TelemetryObservation CreateObservation(
        string processName,
        string time,
        params (TelemetryMetricKind Kind, double Value)[] metrics) =>
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
