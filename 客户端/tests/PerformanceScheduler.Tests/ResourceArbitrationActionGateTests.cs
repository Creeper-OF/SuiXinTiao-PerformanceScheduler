using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ResourceArbitrationActionGateTests
{
    private readonly ResourceArbitrationActionGate _gate = new();

    [Fact]
    public void Evaluate_WhenDisabled_PreservesExistingBackgroundPolicyBehavior()
    {
        var result = _gate.Evaluate(enabled: false, summary: null);

        Assert.False(result.IsEnabled);
        Assert.True(result.AllowBackgroundActions);
        Assert.Empty(result.AllowedProcessIds);
    }

    [Fact]
    public void Evaluate_WhenEvidenceIsMissing_BlocksBackgroundActions()
    {
        var result = _gate.Evaluate(enabled: true, summary: null);

        Assert.True(result.IsEnabled);
        Assert.False(result.AllowBackgroundActions);
        Assert.Equal("ResourceArbitration.MissingEvidence", result.ReasonKey);
    }

    [Fact]
    public void Evaluate_WhenContentionHasRecommendations_AllowsOnlyRecommendedProcesses()
    {
        var compilerStartedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var browserStartedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var result = _gate.Evaluate(
            enabled: true,
            summary: new ResourceArbitrationSummary
            {
                State = ResourceArbitrationState.RecommendBackgroundRelief,
                Recommendations =
                [
                    new ResourceArbitrationRecommendation
                    {
                        ProcessId = 100,
                        ProcessName = "compiler",
                        StartedAt = compilerStartedAt
                    },
                    new ResourceArbitrationRecommendation
                    {
                        ProcessId = 101,
                        ProcessName = "browser",
                        StartedAt = browserStartedAt
                    }
                ]
            });

        Assert.True(result.AllowBackgroundActions);
        Assert.Equal([100, 101], result.AllowedProcessIds.OrderBy(id => id).ToArray());
        Assert.Equal(compilerStartedAt, result.AllowedProcessStartTimes[100]);
        Assert.Equal(browserStartedAt, result.AllowedProcessStartTimes[101]);
    }

    [Fact]
    public void Evaluate_WhenRecommendationIdentityIsMissing_BlocksBackgroundActions()
    {
        var result = _gate.Evaluate(
            enabled: true,
            summary: new ResourceArbitrationSummary
            {
                State = ResourceArbitrationState.RecommendBackgroundRelief,
                Recommendations =
                [
                    new ResourceArbitrationRecommendation { ProcessId = 100, ProcessName = "compiler" }
                ]
            });

        Assert.False(result.AllowBackgroundActions);
        Assert.Equal("ResourceArbitration.CandidateIdentityUnavailable", result.ReasonKey);
        Assert.Empty(result.AllowedProcessStartTimes);
    }

    [Fact]
    public void Evaluate_WhenThereIsNoContention_DoesNotThrottleAnyBackgroundProcess()
    {
        var result = _gate.Evaluate(
            enabled: true,
            summary: new ResourceArbitrationSummary
            {
                State = ResourceArbitrationState.NoContention,
                ReasonKey = "ResourceArbitration.NoContention"
            });

        Assert.False(result.AllowBackgroundActions);
        Assert.Empty(result.AllowedProcessIds);
    }

    [Fact]
    public void Evaluate_WhenRecommendationListIsEmpty_RemainsBlocked()
    {
        var result = _gate.Evaluate(
            enabled: true,
            summary: new ResourceArbitrationSummary
            {
                State = ResourceArbitrationState.RecommendBackgroundRelief
            });

        Assert.False(result.AllowBackgroundActions);
        Assert.Equal("ResourceArbitration.CandidateMetricsUnavailable", result.ReasonKey);
    }
}
