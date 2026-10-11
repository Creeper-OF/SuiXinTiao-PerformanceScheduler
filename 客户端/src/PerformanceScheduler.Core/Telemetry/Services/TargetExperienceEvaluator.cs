using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Evaluates target-experience constraints from collected telemetry.
/// It never creates or applies a system action.
/// </summary>
public sealed class TargetExperienceEvaluator
{
    public TargetExperienceEvaluation Evaluate(
        TargetExperiencePreference? preference,
        TelemetryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (preference is null || !preference.Enabled)
        {
            return new TargetExperienceEvaluation();
        }

        var constraints = new List<TargetExperienceConstraintEvaluation>(4);
        AddMinimumConstraint(
            constraints,
            observation,
            TelemetryMetricKind.FramesPerSecondAverage,
            preference.TargetFramesPerSecond,
            preference.HysteresisPercent,
            "TargetExperience.FrameRateBelowTarget",
            "TargetExperience.FrameRateWithinTarget");
        AddMaximumConstraint(
            constraints,
            observation,
            TelemetryMetricKind.FrameTimeP95Milliseconds,
            preference.MaximumFrameTimeP95Milliseconds,
            preference.HysteresisPercent,
            "TargetExperience.FrameTimeAboveLimit",
            "TargetExperience.FrameTimeWithinTarget");
        AddMaximumConstraint(
            constraints,
            observation,
            TelemetryMetricKind.ThermalZoneTemperatureCelsius,
            preference.MaximumTemperatureCelsius,
            preference.HysteresisPercent,
            "TargetExperience.TemperatureAboveLimit",
            "TargetExperience.TemperatureWithinTarget",
            alternateKinds: [TelemetryMetricKind.NvidiaGpuTemperatureCelsius]);
        AddMaximumConstraint(
            constraints,
            observation,
            TelemetryMetricKind.NvidiaGpuPowerWatts,
            preference.MaximumPowerWatts,
            preference.HysteresisPercent,
            "TargetExperience.PowerAboveLimit",
            "TargetExperience.PowerWithinTarget");

        if (constraints.Count == 0)
        {
            return new TargetExperienceEvaluation
            {
                IsConfigured = true,
                State = TargetExperienceState.InsufficientEvidence,
                ReasonKey = "TargetExperience.NoNumericConstraint"
            };
        }

        var state = constraints.Any(constraint => constraint.Status == TargetExperienceConstraintStatus.AboveLimit)
            ? TargetExperienceState.NeedsCoolingOrPower
            : constraints.Any(constraint => constraint.Status == TargetExperienceConstraintStatus.BelowTarget)
                ? TargetExperienceState.NeedsMorePerformance
                : constraints.All(constraint => constraint.Status == TargetExperienceConstraintStatus.WithinTarget)
                    ? TargetExperienceState.OnTarget
                    : TargetExperienceState.InsufficientEvidence;

        return new TargetExperienceEvaluation
        {
            IsConfigured = true,
            State = state,
            CanRecommendAdjustment = state is TargetExperienceState.NeedsMorePerformance or TargetExperienceState.NeedsCoolingOrPower,
            // The apply pipeline is intentionally not enabled by this evaluator.
            CanApplyAutomatically = false,
            Constraints = constraints,
            ReasonKey = state switch
            {
                TargetExperienceState.OnTarget => "TargetExperience.OnTarget",
                TargetExperienceState.NeedsMorePerformance => "TargetExperience.NeedsMorePerformance",
                TargetExperienceState.NeedsCoolingOrPower => "TargetExperience.NeedsCoolingOrPower",
                _ => "TargetExperience.InsufficientEvidence"
            }
        };
    }

    private static void AddMinimumConstraint(
        ICollection<TargetExperienceConstraintEvaluation> constraints,
        TelemetryObservation observation,
        TelemetryMetricKind metricKind,
        int? target,
        double hysteresisPercent,
        string belowReasonKey,
        string withinReasonKey)
    {
        if (target is null)
        {
            return;
        }

        var actual = FindMetric(observation, metricKind);
        var lowerBound = target.Value * (1 - NormalizeHysteresis(hysteresisPercent));
        var status = actual is null
            ? TargetExperienceConstraintStatus.Unknown
            : actual.Value < lowerBound
                ? TargetExperienceConstraintStatus.BelowTarget
                : TargetExperienceConstraintStatus.WithinTarget;
        constraints.Add(new TargetExperienceConstraintEvaluation
        {
            MetricKind = metricKind,
            ActualValue = actual,
            TargetValue = target,
            Status = status,
            ReasonKey = actual is null
                ? "TargetExperience.MetricUnavailable"
                : status == TargetExperienceConstraintStatus.BelowTarget
                    ? belowReasonKey
                    : withinReasonKey
        });
    }

    private static void AddMaximumConstraint(
        ICollection<TargetExperienceConstraintEvaluation> constraints,
        TelemetryObservation observation,
        TelemetryMetricKind metricKind,
        double? target,
        double hysteresisPercent,
        string aboveReasonKey,
        string withinReasonKey,
        IReadOnlyList<TelemetryMetricKind>? alternateKinds = null)
    {
        if (target is null)
        {
            return;
        }

        var actual = FindMaxMetric(observation, metricKind, alternateKinds);
        var upperBound = target.Value * (1 + NormalizeHysteresis(hysteresisPercent));
        var status = actual is null
            ? TargetExperienceConstraintStatus.Unknown
            : actual.Value > upperBound
                ? TargetExperienceConstraintStatus.AboveLimit
                : TargetExperienceConstraintStatus.WithinTarget;
        constraints.Add(new TargetExperienceConstraintEvaluation
        {
            MetricKind = metricKind,
            ActualValue = actual,
            TargetValue = target,
            Status = status,
            ReasonKey = actual is null
                ? "TargetExperience.MetricUnavailable"
                : status == TargetExperienceConstraintStatus.AboveLimit
                    ? aboveReasonKey
                    : withinReasonKey
        });
    }

    private static double? FindMaxMetric(
        TelemetryObservation observation,
        TelemetryMetricKind primaryKind,
        IReadOnlyList<TelemetryMetricKind>? alternateKinds)
    {
        var primaryVal = FindMetric(observation, primaryKind);
        if (alternateKinds is null || alternateKinds.Count == 0)
        {
            return primaryVal;
        }

        var candidates = new List<double>();
        if (primaryVal is not null) candidates.Add(primaryVal.Value);
        foreach (var alt in alternateKinds)
        {
            var altVal = FindMetric(observation, alt);
            if (altVal is not null) candidates.Add(altVal.Value);
        }

        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static double? FindMetric(
        TelemetryObservation observation,
        TelemetryMetricKind metricKind)
    {
        var values = TelemetryObservationSampleSelector.CollectedSamples(observation)
            .SelectMany(sample => sample.Metrics)
            .Where(metric => metric.Kind == metricKind)
            .Select(metric => metric.Value)
            .Where(double.IsFinite)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double NormalizeHysteresis(double value) =>
        Math.Clamp(double.IsFinite(value) ? value / 100 : 0, 0, 0.5);
}
