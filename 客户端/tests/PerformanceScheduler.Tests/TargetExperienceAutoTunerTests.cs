using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TargetExperienceAutoTunerTests
{
    private readonly TargetExperienceAutoTuner _tuner = new();
    private static readonly DateTimeOffset BaselineTime = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlanAdjustment_WhenPreferenceIsNull_ReturnsNull()
    {
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.IncreasePerformance);

        var plan = _tuner.PlanAdjustment(null, decision, currentProcessorMaxPercent: 90, lastAdjustedAt: null, now: BaselineTime);

        Assert.Null(plan);
    }

    [Fact]
    public void PlanAdjustment_WhenControlModeIsNotAutomatic_ReturnsNull()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.RecommendOnly
        };
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.IncreasePerformance, canApply: false);

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 90, lastAdjustedAt: null, now: BaselineTime);

        Assert.Null(plan);
    }

    [Fact]
    public void PlanAdjustment_WhenDecisionIsNotStable_ReturnsNull()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.Automatic
        };
        var decision = CreateDecision(isStable: false, kind: TargetExperienceAdjustmentKind.IncreasePerformance);

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 90, lastAdjustedAt: null, now: BaselineTime);

        Assert.Null(plan);
    }

    [Fact]
    public void PlanAdjustment_WhenInCooldown_ReturnsInCooldownPlan()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.Automatic
        };
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.IncreasePerformance);
        var lastAdjusted = BaselineTime.AddSeconds(-5); // Within 10s cooldown

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 85, lastAdjustedAt: lastAdjusted, now: BaselineTime);

        Assert.NotNull(plan);
        Assert.True(plan.IsInCooldown);
        Assert.False(plan.HasChange);
        Assert.Equal("TargetExperience.InCooldown", plan.ReasonKey);
    }

    [Fact]
    public void PlanAdjustment_IncreasePerformance_StepsUpWithinEnvelope()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.Automatic
        };
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.IncreasePerformance);
        var lastAdjusted = BaselineTime.AddSeconds(-15); // Past 10s cooldown

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 80, lastAdjustedAt: lastAdjusted, now: BaselineTime);

        Assert.NotNull(plan);
        Assert.False(plan.IsInCooldown);
        Assert.True(plan.HasChange);
        Assert.Equal(80, plan.CurrentProcessorMaxPercent);
        Assert.Equal(85, plan.TargetProcessorMaxPercent); // Default StepUp is 5%
        Assert.Equal("TargetExperience.AutomaticReady", plan.ReasonKey);
    }

    [Fact]
    public void PlanAdjustment_IncreasePerformance_WhenAtMax_ReturnsBoundaryReached()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.Automatic
        };
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.IncreasePerformance);

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 100, lastAdjustedAt: null, now: BaselineTime);

        Assert.NotNull(plan);
        Assert.True(plan.IsBoundaryReached);
        Assert.False(plan.HasChange);
        Assert.Equal(100, plan.TargetProcessorMaxPercent);
        Assert.Equal("TargetExperience.EnvelopeMaxReached", plan.ReasonKey);
    }

    [Fact]
    public void PlanAdjustment_ReduceThermalOrPower_StepsDownWithinEnvelope()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.Automatic
        };
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.ReduceThermalOrPower);

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 95, lastAdjustedAt: null, now: BaselineTime);

        Assert.NotNull(plan);
        Assert.True(plan.HasChange);
        Assert.Equal(95, plan.CurrentProcessorMaxPercent);
        Assert.Equal(90, plan.TargetProcessorMaxPercent); // Default StepDown is 5%
        Assert.Equal("TargetExperience.AutomaticReady", plan.ReasonKey);
    }

    [Fact]
    public void PlanAdjustment_ReduceThermalOrPower_WhenAtMin_ReturnsBoundaryReached()
    {
        var preference = new TargetExperiencePreference
        {
            Enabled = true,
            ControlMode = TargetExperienceControlMode.Automatic
        };
        var decision = CreateDecision(isStable: true, kind: TargetExperienceAdjustmentKind.ReduceThermalOrPower);

        var plan = _tuner.PlanAdjustment(preference, decision, currentProcessorMaxPercent: 60, lastAdjustedAt: null, now: BaselineTime);

        Assert.NotNull(plan);
        Assert.True(plan.IsBoundaryReached);
        Assert.False(plan.HasChange);
        Assert.Equal(60, plan.TargetProcessorMaxPercent);
        Assert.Equal("TargetExperience.EnvelopeMinReached", plan.ReasonKey);
    }

    private static TargetExperienceControlDecision CreateDecision(
        bool isStable,
        TargetExperienceAdjustmentKind kind,
        bool canApply = true) =>
        new()
        {
            State = kind == TargetExperienceAdjustmentKind.IncreasePerformance
                ? TargetExperienceControlState.NeedsMorePerformance
                : TargetExperienceControlState.NeedsCoolingOrPower,
            IsStable = isStable,
            EvidenceSampleCount = 5,
            StableSampleCount = 3,
            Recommendation = new TargetExperienceAdjustmentRecommendation
            {
                Kind = kind,
                StableSampleCount = 3,
                CanApplyAutomatically = canApply,
                ReasonKey = "TargetExperience.AutomaticReady"
            }
        };
}
