using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TelemetrySchedulingComparisonEvaluatorTests
{
    private readonly TelemetrySchedulingComparisonEvaluator _evaluator = new();

    [Fact]
    public void Compare_WithBeforeAndAfterSamples_ReturnsDeltas()
    {
        var result = _evaluator.Compare(
            CreateRun("game", "12:01"),
            [
                CreateHistory("game", "12:02", 60),
                CreateHistory("game", "12:00", 40)
            ]);

        Assert.True(result.HasEligibleRun);
        Assert.True(result.HasBeforeObservation);
        Assert.True(result.HasAfterObservation);
        Assert.Equal(20, Assert.Single(result.Deltas).Delta);
    }

    [Fact]
    public void Compare_WithCurrentObservation_UsesItAsAfterSample()
    {
        var result = _evaluator.Compare(
            CreateRun("game", "12:01"),
            [CreateHistory("game", "12:00", 40)],
            CreateObservation("game", "12:02", 60));

        Assert.True(result.HasAfterObservation);
        Assert.Equal(20, Assert.Single(result.Deltas).Delta);
    }

    [Fact]
    public void Compare_WithMissingBeforeSample_ReportsInsufficientEvidence()
    {
        var result = _evaluator.Compare(
            CreateRun("game", "12:01"),
            [CreateHistory("game", "12:02", 60)]);

        Assert.True(result.HasEligibleRun);
        Assert.False(result.HasBeforeObservation);
        Assert.True(result.HasAfterObservation);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void Compare_WithFailedRun_DoesNotClaimAnEffect()
    {
        var result = _evaluator.Compare(
            CreateRun("game", "12:01", success: false),
            [
                CreateHistory("game", "12:00", 40),
                CreateHistory("game", "12:02", 60)
            ]);

        Assert.False(result.HasEligibleRun);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void Compare_WithDifferentProcessHistory_DoesNotMixApplications()
    {
        var result = _evaluator.Compare(
            CreateRun("game", "12:01"),
            [
                CreateHistory("browser", "12:00", 40),
                CreateHistory("browser", "12:02", 60)
            ]);

        Assert.True(result.HasEligibleRun);
        Assert.False(result.HasBeforeObservation);
        Assert.False(result.HasAfterObservation);
        Assert.Empty(result.Deltas);
    }

    private static SchedulerRunHistoryEntry CreateRun(
        string processName,
        string time,
        bool success = true) =>
        new()
        {
            RunId = Guid.NewGuid(),
            OccurredAt = ParseTime(time),
            ProcessName = processName,
            ProfileName = "Test Profile",
            Success = success
        };

    private static TelemetryObservationHistoryEntry CreateHistory(
        string processName,
        string time,
        double cpuUsage) =>
        new()
        {
            StorageId = 1,
            Observation = CreateObservation(processName, time, cpuUsage)
        };

    private static TelemetryObservation CreateObservation(
        string processName,
        string time,
        double cpuUsage) =>
        new()
        {
            StartedAt = ParseTime(time),
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
                    Metrics =
                    [
                        new TelemetryMetricValue
                        {
                            Kind = TelemetryMetricKind.ProcessCpuUsagePercent,
                            Value = cpuUsage,
                            Unit = "%"
                        }
                    ]
                }
            ]
        };

    private static DateTimeOffset ParseTime(string time) =>
        DateTimeOffset.Parse($"2026-08-05T{time}:00Z");
}
