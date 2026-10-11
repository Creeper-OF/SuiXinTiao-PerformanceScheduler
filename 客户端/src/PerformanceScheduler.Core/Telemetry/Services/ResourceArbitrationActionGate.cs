using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Converts an explicit arbitration result into a conservative background-action
/// allow-list. It is opt-in and never invents a candidate when evidence is missing.
/// </summary>
public sealed class ResourceArbitrationActionGate
{
    public ResourceArbitrationActionDecision Evaluate(
        bool enabled,
        ResourceArbitrationSummary? summary)
    {
        if (!enabled)
        {
            return new ResourceArbitrationActionDecision();
        }

        if (summary is null)
        {
            return new ResourceArbitrationActionDecision
            {
                IsEnabled = true,
                AllowBackgroundActions = false,
                ReasonKey = "ResourceArbitration.MissingEvidence"
            };
        }

        if (summary.State != ResourceArbitrationState.RecommendBackgroundRelief)
        {
            return new ResourceArbitrationActionDecision
            {
                IsEnabled = true,
                AllowBackgroundActions = false,
                ReasonKey = summary.ReasonKey
            };
        }

        var allowedProcessIds = summary.Recommendations
            .Select(recommendation => recommendation.ProcessId)
            .Where(processId => processId > 0)
            .ToHashSet();
        if (allowedProcessIds.Count == 0)
        {
            return new ResourceArbitrationActionDecision
            {
                IsEnabled = true,
                AllowBackgroundActions = false,
                ReasonKey = "ResourceArbitration.CandidateMetricsUnavailable"
            };
        }

        var allowedProcessStartTimes = summary.Recommendations
            .Where(recommendation => recommendation.ProcessId > 0)
            .Select(recommendation =>
                (recommendation.ProcessId, recommendation.StartedAt))
            .ToArray();
        if (allowedProcessStartTimes.Any(item => item.StartedAt is null))
        {
            return new ResourceArbitrationActionDecision
            {
                IsEnabled = true,
                AllowBackgroundActions = false,
                ReasonKey = "ResourceArbitration.CandidateIdentityUnavailable"
            };
        }

        return new ResourceArbitrationActionDecision
        {
            IsEnabled = true,
            AllowBackgroundActions = true,
            AllowedProcessIds = allowedProcessIds,
            AllowedProcessStartTimes = allowedProcessStartTimes
                .ToDictionary(item => item.ProcessId, item => item.StartedAt!.Value),
            ReasonKey = "ResourceArbitration.ApplyRecommendedCandidates"
        };
    }
}
