using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Aggregates read-only telemetry diagnostics for one foreground observation.
/// It produces evidence only and never creates or executes a scheduling action.
/// </summary>
public sealed class TelemetryAnalysisEvaluator
{
    private readonly ResourcePressureEvaluator _resourcePressureEvaluator = new();
    private readonly ResourceContentionEvaluator _resourceContentionEvaluator = new();
    private readonly ResourceAdvisoryEvaluator _resourceAdvisoryEvaluator = new();
    private readonly ResourceArbitrationEvaluator _resourceArbitrationEvaluator = new();
    private readonly TargetExperienceEvaluator _targetExperienceEvaluator = new();
    private readonly TargetExperienceController _targetExperienceController = new();
    private readonly TelemetryComparisonEvaluator _telemetryComparisonEvaluator = new();
    private readonly TelemetryTrendEvaluator _telemetryTrendEvaluator = new();
    private readonly TelemetrySchedulingComparisonEvaluator _telemetrySchedulingComparisonEvaluator = new();

    public TelemetryAnalysisSummary Evaluate(
        TelemetryObservation current,
        IEnumerable<TelemetryObservationHistoryEntry> history,
        SchedulerRunHistoryEntry? latestSuccessfulRun = null,
        TargetExperiencePreference? targetExperience = null,
        IEnumerable<ResourceArbitrationCandidate>? arbitrationCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);

        var historyEntries = history.ToArray();
        var previousEntries = historyEntries
            .Where(entry => entry.Observation.StartedAt < current.StartedAt)
            .ToArray();
        var pressure = _resourcePressureEvaluator.Evaluate(current);
        var contention = _resourceContentionEvaluator.Evaluate(current);
        var previous = previousEntries
            .Where(entry => string.Equals(
                entry.Observation.Target.ProcessName,
                current.Target.ProcessName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Observation.StartedAt)
            .Select(entry => entry.Observation)
            .FirstOrDefault();
        var targetEvaluation = _targetExperienceEvaluator.Evaluate(targetExperience, current);
        var targetEvidence = previousEntries
            .Where(entry => entry.Observation.Target.ProcessId == current.Target.ProcessId)
            .Where(entry => string.Equals(
                entry.Observation.Target.ProcessName,
                current.Target.ProcessName,
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => new TargetExperienceEvidenceSample
            {
                ObservedAt = entry.Observation.StartedAt,
                Evaluation = _targetExperienceEvaluator.Evaluate(targetExperience, entry.Observation)
            })
            .ToArray();
        var targetControl = _targetExperienceController.Evaluate(
            targetExperience,
            new TargetExperienceEvidenceSample
            {
                ObservedAt = current.StartedAt,
                Evaluation = targetEvaluation
            },
            targetEvidence);

        return new TelemetryAnalysisSummary
        {
            ResourcePressure = pressure,
            ResourceContention = contention,
            ResourceAdvisory = _resourceAdvisoryEvaluator.Evaluate(pressure, contention),
            ResourceArbitration = _resourceArbitrationEvaluator.Evaluate(
                contention,
                current.Target.ProcessId,
                arbitrationCandidates),
            TargetExperience = targetEvaluation,
            TargetExperienceControl = targetControl,
            Comparison = _telemetryComparisonEvaluator.Compare(current, previous),
            SchedulingComparison = _telemetrySchedulingComparisonEvaluator.Compare(
                latestSuccessfulRun,
                historyEntries,
                current),
            Trends = _telemetryTrendEvaluator.Evaluate(
                current,
                previousEntries,
                maximumSamples: 5)
        };
    }
}
