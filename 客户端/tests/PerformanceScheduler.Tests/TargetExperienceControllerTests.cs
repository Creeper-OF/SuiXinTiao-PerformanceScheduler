using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TargetExperienceControllerTests
{
    private readonly TargetExperienceController _controller = new();

    [Fact]
    public void Evaluate_WhenEvidenceIsNotStable_DoesNotRecommendAnAdjustment()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _controller.Evaluate(
            Preference(minimumStableSamples: 3),
            Sample(now, TargetExperienceState.NeedsMorePerformance),
            [Sample(now.AddSeconds(-1), TargetExperienceState.NeedsMorePerformance)]);

        Assert.Equal(TargetExperienceControlState.AwaitingStableEvidence, result.State);
        Assert.False(result.IsStable);
        Assert.Equal(2, result.StableSampleCount);
        Assert.Null(result.Recommendation);
    }

    [Fact]
    public void Evaluate_WhenStateIsStable_ReturnsRecommendationWithoutApplyPermission()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _controller.Evaluate(
            Preference(minimumStableSamples: 3),
            Sample(now, TargetExperienceState.NeedsCoolingOrPower),
            [
                Sample(now.AddSeconds(-1), TargetExperienceState.NeedsCoolingOrPower),
                Sample(now.AddSeconds(-2), TargetExperienceState.NeedsCoolingOrPower)
            ]);

        Assert.Equal(TargetExperienceControlState.NeedsCoolingOrPower, result.State);
        Assert.True(result.IsStable);
        Assert.NotNull(result.Recommendation);
        var recommendation = result.Recommendation!;
        Assert.Equal(TargetExperienceAdjustmentKind.ReduceThermalOrPower, recommendation.Kind);
        Assert.False(recommendation.CanApplyAutomatically);
        Assert.True(recommendation.RequiresCapabilityValidation);
    }

    [Fact]
    public void Evaluate_WhenControlModeIsAutomatic_ReturnsRecommendationWithCanApplyAutomaticallyTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _controller.Evaluate(
            Preference(minimumStableSamples: 3, controlMode: TargetExperienceControlMode.Automatic),
            Sample(now, TargetExperienceState.NeedsMorePerformance),
            [
                Sample(now.AddSeconds(-1), TargetExperienceState.NeedsMorePerformance),
                Sample(now.AddSeconds(-2), TargetExperienceState.NeedsMorePerformance)
            ]);

        Assert.Equal(TargetExperienceControlState.NeedsMorePerformance, result.State);
        Assert.True(result.IsStable);
        Assert.NotNull(result.Recommendation);
        var recommendation = result.Recommendation!;
        Assert.Equal(TargetExperienceAdjustmentKind.IncreasePerformance, recommendation.Kind);
        Assert.True(recommendation.CanApplyAutomatically);
        Assert.Equal("TargetExperience.AutomaticReady", recommendation.ReasonKey);
    }

    [Fact]
    public void Evaluate_WhenARecentSampleChangesState_ResetsTheStableCount()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _controller.Evaluate(
            Preference(minimumStableSamples: 2),
            Sample(now, TargetExperienceState.NeedsMorePerformance),
            [
                Sample(now.AddSeconds(-1), TargetExperienceState.OnTarget),
                Sample(now.AddSeconds(-2), TargetExperienceState.NeedsMorePerformance)
            ]);

        Assert.Equal(TargetExperienceControlState.AwaitingStableEvidence, result.State);
        Assert.Equal(1, result.StableSampleCount);
        Assert.Null(result.Recommendation);
    }

    [Fact]
    public void Evaluate_WhenObservationOnly_IsStableButDoesNotExposeRecommendation()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _controller.Evaluate(
            Preference(minimumStableSamples: 1, controlMode: TargetExperienceControlMode.ObservationOnly),
            Sample(now, TargetExperienceState.NeedsMorePerformance));

        Assert.Equal(TargetExperienceControlState.NeedsMorePerformance, result.State);
        Assert.True(result.IsStable);
        Assert.Null(result.Recommendation);
        Assert.Equal("TargetExperience.ObservationOnly", result.ReasonKey);
    }

    [Fact]
    public void Evaluate_WhenSampleIsOutsideObservationWindow_DoesNotUseItAsEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var result = _controller.Evaluate(
            Preference(minimumStableSamples: 2, observationWindowSeconds: 5),
            Sample(now, TargetExperienceState.NeedsMorePerformance),
            [Sample(now.AddSeconds(-20), TargetExperienceState.NeedsMorePerformance)]);

        Assert.Equal(TargetExperienceControlState.AwaitingStableEvidence, result.State);
        Assert.Equal(1, result.EvidenceSampleCount);
        Assert.Equal(1, result.StableSampleCount);
    }

    private static TargetExperiencePreference Preference(
        int minimumStableSamples,
        TargetExperienceControlMode controlMode = TargetExperienceControlMode.RecommendOnly,
        int observationWindowSeconds = 10) =>
        new()
        {
            Enabled = true,
            TargetFramesPerSecond = 60,
            MinimumStableSamples = minimumStableSamples,
            ObservationWindowSeconds = observationWindowSeconds,
            ControlMode = controlMode
        };

    private static TargetExperienceEvidenceSample Sample(
        DateTimeOffset observedAt,
        TargetExperienceState state) =>
        new()
        {
            ObservedAt = observedAt,
            Evaluation = new TargetExperienceEvaluation
            {
                IsConfigured = true,
                State = state,
                CanRecommendAdjustment = state is TargetExperienceState.NeedsMorePerformance or TargetExperienceState.NeedsCoolingOrPower
            }
        };
}
