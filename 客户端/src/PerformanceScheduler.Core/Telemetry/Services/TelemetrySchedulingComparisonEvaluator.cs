using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Compares same-process telemetry immediately before and after a successful scheduling run.
/// Missing samples or mismatched providers remain an explicit diagnostic state.
/// </summary>
public sealed class TelemetrySchedulingComparisonEvaluator
{
    private readonly TelemetryComparisonEvaluator _telemetryComparisonEvaluator;

    public TelemetrySchedulingComparisonEvaluator(
        TelemetryComparisonEvaluator? telemetryComparisonEvaluator = null)
    {
        _telemetryComparisonEvaluator = telemetryComparisonEvaluator ?? new TelemetryComparisonEvaluator();
    }

    public TelemetrySchedulingComparisonSummary Compare(
        SchedulerRunHistoryEntry? run,
        IEnumerable<TelemetryObservationHistoryEntry> history,
        TelemetryObservation? current = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (run is null ||
            !run.Success ||
            string.IsNullOrWhiteSpace(run.ProcessName))
        {
            return new TelemetrySchedulingComparisonSummary();
        }

        var observations = history
            .Where(entry => string.Equals(
                entry.Observation.Target.ProcessName,
                run.ProcessName,
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Observation)
            .ToArray();

        var before = observations
            .Where(observation => observation.StartedAt <= run.OccurredAt)
            .OrderByDescending(observation => observation.StartedAt)
            .FirstOrDefault();
        var after = observations
            .Where(observation => observation.StartedAt > run.OccurredAt)
            .OrderBy(observation => observation.StartedAt)
            .FirstOrDefault();

        if (current is not null &&
            string.Equals(
                current.Target.ProcessName,
                run.ProcessName,
                StringComparison.OrdinalIgnoreCase) &&
            current.StartedAt > run.OccurredAt &&
            (after is null || current.StartedAt < after.StartedAt))
        {
            after = current;
        }

        if (before is null || after is null)
        {
            return new TelemetrySchedulingComparisonSummary
            {
                HasEligibleRun = true,
                HasBeforeObservation = before is not null,
                HasAfterObservation = after is not null,
                RunId = run.RunId,
                ProfileName = run.ProfileName
            };
        }

        var comparison = _telemetryComparisonEvaluator.Compare(after, before);
        return new TelemetrySchedulingComparisonSummary
        {
            HasEligibleRun = true,
            HasBeforeObservation = true,
            HasAfterObservation = true,
            RunId = run.RunId,
            ProfileName = run.ProfileName,
            Deltas = comparison.Deltas
        };
    }
}
