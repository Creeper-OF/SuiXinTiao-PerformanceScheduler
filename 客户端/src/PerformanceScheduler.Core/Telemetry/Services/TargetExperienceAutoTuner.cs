using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Evaluates stable target-experience decisions into a bounded, rate-limited auto-tuning plan.
/// It enforces safe envelopes, anti-oscillation cooldowns, and never mutates system state directly.
/// </summary>
public sealed class TargetExperienceAutoTuner
{
    private readonly TargetExperienceAutoTuningEnvelope _envelope;

    public TargetExperienceAutoTuner(TargetExperienceAutoTuningEnvelope? envelope = null)
    {
        _envelope = envelope ?? TargetExperienceAutoTuningEnvelope.Default;
    }

    public TargetExperienceAutoTuningEnvelope Envelope => _envelope;

    public TargetExperienceAutoTuningPlan? PlanAdjustment(
        TargetExperiencePreference? preference,
        TargetExperienceControlDecision decision,
        int currentProcessorMaxPercent,
        DateTimeOffset? lastAdjustedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (preference is null || !preference.Enabled || preference.ControlMode != TargetExperienceControlMode.Automatic)
        {
            return null;
        }

        if (!decision.IsStable || decision.Recommendation is null)
        {
            return null;
        }

        var recommendation = decision.Recommendation;
        if (!recommendation.CanApplyAutomatically)
        {
            return null;
        }

        var clampedCurrent = Math.Clamp(
            currentProcessorMaxPercent,
            _envelope.MinProcessorMaxStatePercent,
            _envelope.MaxProcessorMaxStatePercent);

        if (lastAdjustedAt is { } previous && now - previous < _envelope.Cooldown)
        {
            return new TargetExperienceAutoTuningPlan
            {
                Kind = recommendation.Kind,
                CurrentProcessorMaxPercent = currentProcessorMaxPercent,
                TargetProcessorMaxPercent = currentProcessorMaxPercent,
                ReasonKey = "TargetExperience.InCooldown",
                IsInCooldown = true
            };
        }

        if (recommendation.Kind == TargetExperienceAdjustmentKind.IncreasePerformance)
        {
            var target = Math.Min(_envelope.MaxProcessorMaxStatePercent, clampedCurrent + _envelope.StepUpPercent);
            if (target <= currentProcessorMaxPercent && currentProcessorMaxPercent >= _envelope.MaxProcessorMaxStatePercent)
            {
                return new TargetExperienceAutoTuningPlan
                {
                    Kind = recommendation.Kind,
                    CurrentProcessorMaxPercent = currentProcessorMaxPercent,
                    TargetProcessorMaxPercent = currentProcessorMaxPercent,
                    ReasonKey = "TargetExperience.EnvelopeMaxReached",
                    IsBoundaryReached = true
                };
            }

            return new TargetExperienceAutoTuningPlan
            {
                Kind = recommendation.Kind,
                CurrentProcessorMaxPercent = currentProcessorMaxPercent,
                TargetProcessorMaxPercent = target,
                ReasonKey = "TargetExperience.AutomaticReady"
            };
        }

        if (recommendation.Kind == TargetExperienceAdjustmentKind.ReduceThermalOrPower)
        {
            var target = Math.Max(_envelope.MinProcessorMaxStatePercent, clampedCurrent - _envelope.StepDownPercent);
            if (target >= currentProcessorMaxPercent && currentProcessorMaxPercent <= _envelope.MinProcessorMaxStatePercent)
            {
                return new TargetExperienceAutoTuningPlan
                {
                    Kind = recommendation.Kind,
                    CurrentProcessorMaxPercent = currentProcessorMaxPercent,
                    TargetProcessorMaxPercent = currentProcessorMaxPercent,
                    ReasonKey = "TargetExperience.EnvelopeMinReached",
                    IsBoundaryReached = true
                };
            }

            return new TargetExperienceAutoTuningPlan
            {
                Kind = recommendation.Kind,
                CurrentProcessorMaxPercent = currentProcessorMaxPercent,
                TargetProcessorMaxPercent = target,
                ReasonKey = "TargetExperience.AutomaticReady"
            };
        }

        return null;
    }
}
