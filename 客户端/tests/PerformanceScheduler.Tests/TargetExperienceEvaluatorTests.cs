using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TargetExperienceEvaluatorTests
{
    private readonly TargetExperienceEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WhenDisabled_ReturnsDisabledWithoutRecommendations()
    {
        var result = _evaluator.Evaluate(
            new TargetExperiencePreference(),
            CreateObservation((TelemetryMetricKind.FramesPerSecondAverage, 30)));

        Assert.Equal(TargetExperienceState.Disabled, result.State);
        Assert.False(result.IsConfigured);
        Assert.False(result.CanRecommendAdjustment);
        Assert.False(result.CanApplyAutomatically);
    }

    [Fact]
    public void Evaluate_WhenFrameRateIsBelowHysteresisBand_RecommendsMorePerformance()
    {
        var result = _evaluator.Evaluate(
            new TargetExperiencePreference
            {
                Enabled = true,
                TargetFramesPerSecond = 60,
                HysteresisPercent = 5,
                ControlMode = TargetExperienceControlMode.RecommendOnly
            },
            CreateObservation((TelemetryMetricKind.FramesPerSecondAverage, 55)));

        Assert.Equal(TargetExperienceState.NeedsMorePerformance, result.State);
        Assert.True(result.CanRecommendAdjustment);
        Assert.False(result.CanApplyAutomatically);
        Assert.Equal(TargetExperienceConstraintStatus.BelowTarget, Assert.Single(result.Constraints).Status);
    }

    [Fact]
    public void Evaluate_WhenThermalLimitAndFrameRateBothFail_PrioritizesCooling()
    {
        var result = _evaluator.Evaluate(
            new TargetExperiencePreference
            {
                Enabled = true,
                TargetFramesPerSecond = 60,
                MaximumTemperatureCelsius = 80,
                HysteresisPercent = 0
            },
            CreateObservation(
                (TelemetryMetricKind.FramesPerSecondAverage, 30),
                (TelemetryMetricKind.ThermalZoneTemperatureCelsius, 90)));

        Assert.Equal(TargetExperienceState.NeedsCoolingOrPower, result.State);
        Assert.Equal(2, result.Constraints.Count);
    }

    [Fact]
    public void Evaluate_WhenNvidiaGpuTemperatureExceedsLimit_PrioritizesCooling()
    {
        var result = _evaluator.Evaluate(
            new TargetExperiencePreference
            {
                Enabled = true,
                MaximumTemperatureCelsius = 75,
                HysteresisPercent = 0
            },
            CreateObservation(
                (TelemetryMetricKind.ThermalZoneTemperatureCelsius, 60),
                (TelemetryMetricKind.NvidiaGpuTemperatureCelsius, 82)));

        Assert.Equal(TargetExperienceState.NeedsCoolingOrPower, result.State);
        var constraint = Assert.Single(result.Constraints);
        Assert.Equal(82, constraint.ActualValue);
        Assert.Equal(TargetExperienceConstraintStatus.AboveLimit, constraint.Status);
    }

    [Fact]
    public void Evaluate_WhenAllConfiguredMetricsAreWithinLimits_ReturnsOnTarget()
    {
        var result = _evaluator.Evaluate(
            new TargetExperiencePreference
            {
                Enabled = true,
                TargetFramesPerSecond = 60,
                MaximumFrameTimeP95Milliseconds = 20,
                MaximumTemperatureCelsius = 80,
                MaximumPowerWatts = 100,
                HysteresisPercent = 5
            },
            CreateObservation(
                (TelemetryMetricKind.FramesPerSecondAverage, 58),
                (TelemetryMetricKind.FrameTimeP95Milliseconds, 19),
                (TelemetryMetricKind.ThermalZoneTemperatureCelsius, 80),
                (TelemetryMetricKind.NvidiaGpuPowerWatts, 100)));

        Assert.Equal(TargetExperienceState.OnTarget, result.State);
        Assert.False(result.CanRecommendAdjustment);
        Assert.All(result.Constraints, constraint =>
            Assert.Equal(TargetExperienceConstraintStatus.WithinTarget, constraint.Status));
    }

    [Fact]
    public void Evaluate_WhenMetricIsMissing_DoesNotInventAValueOrEnableAutomaticApply()
    {
        var result = _evaluator.Evaluate(
            new TargetExperiencePreference
            {
                Enabled = true,
                MaximumPowerWatts = 100,
                ControlMode = TargetExperienceControlMode.Automatic
            },
            CreateObservation((TelemetryMetricKind.SystemCpuUsagePercent, 80)));

        Assert.Equal(TargetExperienceState.InsufficientEvidence, result.State);
        Assert.Null(Assert.Single(result.Constraints).ActualValue);
        Assert.False(result.CanApplyAutomatically);
    }

    private static TelemetryObservation CreateObservation(
        params (TelemetryMetricKind Kind, double Value)[] metrics) =>
        new()
        {
            StartedAt = DateTimeOffset.UtcNow,
            Target = new FocusedAppContext
            {
                ProcessId = 42,
                ProcessName = "game",
                WindowTitle = "Game",
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
                        Value = metric.Value
                    }).ToArray()
                }
            ]
        };
}
