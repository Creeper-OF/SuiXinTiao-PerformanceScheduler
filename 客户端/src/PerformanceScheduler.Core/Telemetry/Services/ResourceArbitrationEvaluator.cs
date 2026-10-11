using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Produces conservative background-relief recommendations from measured
/// candidates. It never changes process state and never treats missing data as
/// permission to throttle a process.
/// </summary>
public sealed class ResourceArbitrationEvaluator
{
    public ResourceArbitrationSummary Evaluate(
        ResourceContentionSummary contention,
        int foregroundProcessId,
        IEnumerable<ResourceArbitrationCandidate>? candidates,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(contention);

        var candidateList = (candidates ?? Array.Empty<ResourceArbitrationCandidate>())
            .Where(candidate => candidate.ProcessId > 0)
            .Where(candidate => candidate.ProcessId != foregroundProcessId)
            .ToArray();
        var protectedCount = candidateList.Count(IsProtected);
        var measuredCandidates = candidateList
            .Where(candidate => !IsProtected(candidate))
            .Where(candidate => candidate.HasApplicablePolicy)
            .Select(candidate => LoadScore(candidate) is { } loadScore
                ? new MeasuredCandidate(candidate, loadScore)
                : null)
            .OfType<MeasuredCandidate>()
            .OrderByDescending(item => item.LoadScore)
            .ToArray();

        if (!enabled)
        {
            return CreateSummary(
                ResourceArbitrationState.Disabled,
                contention,
                candidateList,
                protectedCount,
                measuredCandidates,
                "ResourceArbitration.Disabled");
        }

        if (contention.Level == ResourceContentionLevel.None)
        {
            return CreateSummary(
                ResourceArbitrationState.NoContention,
                contention,
                candidateList,
                protectedCount,
                measuredCandidates,
                "ResourceArbitration.NoContention");
        }

        if (contention.Level == ResourceContentionLevel.Unknown || !contention.HasComparison)
        {
            return CreateSummary(
                ResourceArbitrationState.InsufficientEvidence,
                contention,
                candidateList,
                protectedCount,
                measuredCandidates,
                "ResourceArbitration.InsufficientEvidence");
        }

        if (measuredCandidates.Length == 0)
        {
            return CreateSummary(
                protectedCount == candidateList.Length
                    ? ResourceArbitrationState.ProtectedOnly
                    : ResourceArbitrationState.InsufficientEvidence,
                contention,
                candidateList,
                protectedCount,
                measuredCandidates,
                protectedCount == candidateList.Length
                    ? "ResourceArbitration.ProtectedOnly"
                    : "ResourceArbitration.CandidateMetricsUnavailable");
        }

        return CreateSummary(
            ResourceArbitrationState.RecommendBackgroundRelief,
            contention,
            candidateList,
            protectedCount,
            measuredCandidates,
            "ResourceArbitration.RecommendBackgroundRelief");
    }

    private static ResourceArbitrationSummary CreateSummary(
        ResourceArbitrationState state,
        ResourceContentionSummary contention,
        IReadOnlyList<ResourceArbitrationCandidate> candidates,
        int protectedCount,
        IReadOnlyList<MeasuredCandidate> measuredCandidates,
        string reasonKey)
    {
        return new ResourceArbitrationSummary
        {
            State = state,
            ContentionLevel = contention.Level,
            CandidateCount = candidates.Count,
            ProtectedCandidateCount = protectedCount,
            MeasuredCandidateCount = measuredCandidates.Count,
            Recommendations = state == ResourceArbitrationState.RecommendBackgroundRelief
                ? measuredCandidates.Select(item => new ResourceArbitrationRecommendation
                {
                    ProcessId = item.Candidate.ProcessId,
                    ProcessName = item.Candidate.ProcessName,
                    Classification = item.Candidate.Classification,
                    LoadScorePercent = item.LoadScore,
                    StartedAt = item.Candidate.StartedAt
                }).ToArray()
                : Array.Empty<ResourceArbitrationRecommendation>(),
            ReasonKey = reasonKey
        };
    }

    private static bool IsProtected(ResourceArbitrationCandidate candidate) =>
        candidate.IsProtected || candidate.Classification == ProcessClassification.System;

    private static double? LoadScore(ResourceArbitrationCandidate candidate)
    {
        var values = new[] { candidate.CpuUsagePercent, candidate.GpuUsagePercent }
            .Where(value => value is { } measured && double.IsFinite(measured) && measured >= 0 && measured <= 100)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private sealed record MeasuredCandidate(
        ResourceArbitrationCandidate Candidate,
        double LoadScore);
}
