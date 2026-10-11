using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Applies the target-experience evidence policy without applying a system change.
/// It is the boundary where stable samples and hysteresis become a recommendation.
/// </summary>
public sealed class TargetExperienceController
{
    public TargetExperienceControlDecision Evaluate(
        TargetExperiencePreference? preference,
        TargetExperienceEvidenceSample current,
        IEnumerable<TargetExperienceEvidenceSample>? history = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (preference is null || !preference.Enabled)
        {
            return new TargetExperienceControlDecision();
        }

        var currentState = MapState(current.Evaluation.State);
        if (currentState is TargetExperienceControlState.Disabled or
            TargetExperienceControlState.InsufficientEvidence)
        {
            return new TargetExperienceControlDecision
            {
                State = TargetExperienceControlState.InsufficientEvidence,
                EvidenceSampleCount = 0,
                ReasonKey = "TargetExperience.InsufficientEvidence"
            };
        }

        var windowSeconds = Math.Clamp(preference.ObservationWindowSeconds, 1, 3600);
        var cutoff = current.ObservedAt - TimeSpan.FromSeconds(windowSeconds);
        var samples = (history ?? Array.Empty<TargetExperienceEvidenceSample>())
            .Append(current)
            .Where(sample => sample.ObservedAt >= cutoff && sample.ObservedAt <= current.ObservedAt)
            .OrderByDescending(sample => sample.ObservedAt)
            .GroupBy(sample => sample.ObservedAt)
            .Select(group => group.First())
            .ToArray();
        var stableSampleCount = CountConsecutiveSamples(samples, currentState);
        var minimumStableSamples = Math.Clamp(preference.MinimumStableSamples, 1, 1000);

        if (stableSampleCount < minimumStableSamples)
        {
            return new TargetExperienceControlDecision
            {
                State = TargetExperienceControlState.AwaitingStableEvidence,
                IsStable = false,
                EvidenceSampleCount = samples.Length,
                StableSampleCount = stableSampleCount,
                ReasonKey = "TargetExperience.AwaitingStableEvidence"
            };
        }

        if (currentState == TargetExperienceControlState.OnTarget)
        {
            return new TargetExperienceControlDecision
            {
                State = currentState,
                IsStable = true,
                EvidenceSampleCount = samples.Length,
                StableSampleCount = stableSampleCount,
                ReasonKey = "TargetExperience.OnTarget"
            };
        }

        var adjustmentKind = currentState == TargetExperienceControlState.NeedsMorePerformance
            ? TargetExperienceAdjustmentKind.IncreasePerformance
            : TargetExperienceAdjustmentKind.ReduceThermalOrPower;
        var recommendationEnabled = preference.ControlMode != TargetExperienceControlMode.ObservationOnly;
        var isAutomatic = preference.ControlMode == TargetExperienceControlMode.Automatic;
        var reasonKey = isAutomatic
            ? "TargetExperience.AutomaticReady"
            : recommendationEnabled
                ? adjustmentKind == TargetExperienceAdjustmentKind.IncreasePerformance
                    ? "TargetExperience.RecommendMorePerformance"
                    : "TargetExperience.RecommendCoolingOrPower"
                : "TargetExperience.ObservationOnly";

        return new TargetExperienceControlDecision
        {
            State = currentState,
            IsStable = true,
            EvidenceSampleCount = samples.Length,
            StableSampleCount = stableSampleCount,
            Recommendation = recommendationEnabled
                ? new TargetExperienceAdjustmentRecommendation
                {
                    Kind = adjustmentKind,
                    StableSampleCount = stableSampleCount,
                    RequiresCapabilityValidation = true,
                    CanApplyAutomatically = isAutomatic,
                    ReasonKey = reasonKey
                }
                : null,
            ReasonKey = reasonKey
        };
    }

    private static int CountConsecutiveSamples(
        IReadOnlyList<TargetExperienceEvidenceSample> samples,
        TargetExperienceControlState currentState)
    {
        var count = 0;
        foreach (var sample in samples)
        {
            if (MapState(sample.Evaluation.State) != currentState)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static TargetExperienceControlState MapState(TargetExperienceState state) =>
        state switch
        {
            TargetExperienceState.OnTarget => TargetExperienceControlState.OnTarget,
            TargetExperienceState.NeedsMorePerformance => TargetExperienceControlState.NeedsMorePerformance,
            TargetExperienceState.NeedsCoolingOrPower => TargetExperienceControlState.NeedsCoolingOrPower,
            TargetExperienceState.Disabled => TargetExperienceControlState.Disabled,
            _ => TargetExperienceControlState.InsufficientEvidence
        };
}
