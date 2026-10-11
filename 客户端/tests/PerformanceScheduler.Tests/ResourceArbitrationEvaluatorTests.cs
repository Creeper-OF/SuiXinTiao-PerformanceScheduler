using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ResourceArbitrationEvaluatorTests
{
    private readonly ResourceArbitrationEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WhenContentionIsHigh_RecommendsMeasuredUnprotectedCandidates()
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = _evaluator.Evaluate(
            HighContention(),
            foregroundProcessId: 42,
            [
                new ResourceArbitrationCandidate
                {
                    ProcessId = 100,
                    ProcessName = "compiler",
                    StartedAt = startedAt,
                    Classification = ProcessClassification.Productivity,
                    HasApplicablePolicy = true,
                    CpuUsagePercent = 72
                },
                new ResourceArbitrationCandidate
                {
                    ProcessId = 101,
                    ProcessName = "recorder",
                    Classification = ProcessClassification.Media,
                    HasApplicablePolicy = true,
                    IsProtected = true,
                    CpuUsagePercent = 95
                },
                new ResourceArbitrationCandidate
                {
                    ProcessId = 102,
                    ProcessName = "game",
                    Classification = ProcessClassification.Game,
                    HasApplicablePolicy = false,
                    CpuUsagePercent = 99
                },
                new ResourceArbitrationCandidate
                {
                    ProcessId = 42,
                    ProcessName = "foreground",
                    HasApplicablePolicy = true,
                    CpuUsagePercent = 100
                },
                new ResourceArbitrationCandidate
                {
                    ProcessId = 103,
                    ProcessName = "service",
                    Classification = ProcessClassification.System,
                    HasApplicablePolicy = true,
                    CpuUsagePercent = 100
                }
            ]);

        Assert.Equal(ResourceArbitrationState.RecommendBackgroundRelief, result.State);
        Assert.Equal(4, result.CandidateCount);
        Assert.Equal(2, result.ProtectedCandidateCount);
        Assert.Equal(1, result.MeasuredCandidateCount);
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(100, recommendation.ProcessId);
        Assert.Equal(72, recommendation.LoadScorePercent);
        Assert.Equal(startedAt, recommendation.StartedAt);
    }

    [Fact]
    public void Evaluate_WhenCandidatesHaveNoMeasurements_RemainsObservationOnly()
    {
        var result = _evaluator.Evaluate(
            HighContention(),
            foregroundProcessId: 42,
            [
                new ResourceArbitrationCandidate
                {
                    ProcessId = 100,
                    ProcessName = "unknown",
                    HasApplicablePolicy = true
                }
            ]);

        Assert.Equal(ResourceArbitrationState.InsufficientEvidence, result.State);
        Assert.Empty(result.Recommendations);
        Assert.Equal(0, result.MeasuredCandidateCount);
    }

    [Fact]
    public void Evaluate_WhenThereIsNoContention_DoesNotRecommendBackgroundRelief()
    {
        var result = _evaluator.Evaluate(
            new ResourceContentionSummary
            {
                Level = ResourceContentionLevel.None,
                ForegroundCpuUsagePercent = 80,
                SystemCpuUsagePercent = 82
            },
            foregroundProcessId: 42,
            [
                new ResourceArbitrationCandidate
                {
                    ProcessId = 100,
                    ProcessName = "compiler",
                    HasApplicablePolicy = true,
                    CpuUsagePercent = 72
                }
            ]);

        Assert.Equal(ResourceArbitrationState.NoContention, result.State);
        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public void Evaluate_WhenDisabled_DoesNotExposeRecommendations()
    {
        var result = _evaluator.Evaluate(
            HighContention(),
            foregroundProcessId: 42,
            [
                new ResourceArbitrationCandidate
                {
                    ProcessId = 100,
                    ProcessName = "compiler",
                    HasApplicablePolicy = true,
                    CpuUsagePercent = 90
                }
            ],
            enabled: false);

        Assert.Equal(ResourceArbitrationState.Disabled, result.State);
        Assert.Empty(result.Recommendations);
    }

    private static ResourceContentionSummary HighContention() =>
        new()
        {
            Level = ResourceContentionLevel.High,
            ForegroundCpuUsagePercent = 35,
            SystemCpuUsagePercent = 94
        };
}
