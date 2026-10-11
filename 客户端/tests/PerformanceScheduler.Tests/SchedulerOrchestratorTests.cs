using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Tests;

public sealed class SchedulerOrchestratorTests
{
    [Fact]
    public async Task EvaluateForegroundAsync_SkipsAdvancedPowerSettingsWhenTargetPlanSwitchFails()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new RecordingRollbackManager();
        var logger = new RecordingLogger();
        var profile = new PerformanceProfile
        {
            Name = "Game Profile",
            Match = new ProfileMatchCriteria
            {
                ProcessNames = new[] { "game" }
            },
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = targetPlan.SchemeGuid,
                Advanced = new PowerPlanAdvancedPreference
                {
                    ProcessorMaxStatePercent = 80
                }
            }
        };

        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            logger);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.True(powerPlanManager.SetActivePlanCalled);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Equal(0, powerPlanManager.GetAdvancedSettingsCallCount);
        Assert.Equal(0, powerPlanManager.ApplyAdvancedSettingsCallCount);
        Assert.Contains(logger.Messages, message => message.Contains("desired power plan is not active", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rollbackManager.LastCapturedState);
        Assert.Equal(activePlan.SchemeGuid, rollbackManager.LastCapturedState.PreviousPowerPlan?.SchemeGuid);
        Assert.Null(rollbackManager.LastCapturedState.PreviousAdvancedPowerSettings);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.PowerPlanSwitch &&
            action.Status == SchedulerActionStatus.Failed);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.AdvancedPowerSettings &&
            action.Status == SchedulerActionStatus.Skipped);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RollsBackWhenPowerPlanSwitchCannotBeVerified()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: true);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Unverified Power Plan",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.Equal(1, powerPlanManager.SetActivePlanCallCount);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.PowerPlanSwitch &&
            action.Status == SchedulerActionStatus.Failed &&
            action.ReasonCode == SchedulerActionReasonCode.ProviderApplyFailed);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_DefersThenRetriesSameSessionAfterFailedRunWasRolledBack()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Retry Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            utcNow: () => now,
            failureRetryBaseDelay: TimeSpan.FromSeconds(5),
            failureRetryMaxDelay: TimeSpan.FromMinutes(1));

        var firstResult = await orchestrator.EvaluateForegroundAsync();
        var deferredResult = await orchestrator.EvaluateForegroundAsync();
        now = now.AddSeconds(5);
        var retriedResult = await orchestrator.EvaluateForegroundAsync();

        Assert.False(firstResult.Success);
        Assert.Equal(1, firstResult.ConsecutiveFailureCount);
        Assert.Equal(now, firstResult.RetryAfter);
        Assert.True(deferredResult.IsRetryDeferred);
        Assert.Null(deferredResult.TransactionId);
        Assert.False(retriedResult.Success);
        Assert.False(retriedResult.IsRetryDeferred);
        Assert.Equal(2, retriedResult.ConsecutiveFailureCount);
        Assert.Equal(now.AddSeconds(10), retriedResult.RetryAfter);
        Assert.NotEqual(firstResult.TransactionId, retriedResult.TransactionId);
        Assert.Equal(2, powerPlanManager.SetActivePlanCallCount);
        Assert.Equal(2, rollbackManager.RollbackCallCount);
        Assert.Contains("can be retried", retriedResult.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreAsync_ClearsFailureCooldownForImmediateRetry()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Manual Retry Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            utcNow: () => now,
            failureRetryBaseDelay: TimeSpan.FromMinutes(1),
            failureRetryMaxDelay: TimeSpan.FromMinutes(1));

        await orchestrator.EvaluateForegroundAsync();
        await orchestrator.RestoreAsync();
        var retriedResult = await orchestrator.EvaluateForegroundAsync();

        Assert.False(retriedResult.IsRetryDeferred);
        Assert.Equal(1, retriedResult.ConsecutiveFailureCount);
        Assert.Equal(2, powerPlanManager.SetActivePlanCallCount);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsFalseAndClearsActiveSessionWhenRollbackRemainsPending()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new IncompleteRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Incomplete Manual Rollback",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var failedRun = await orchestrator.EvaluateForegroundAsync();
        var restored = await orchestrator.RestoreAsync();
        var blockedRun = await orchestrator.EvaluateForegroundAsync();

        Assert.False(failedRun.Success);
        Assert.False(restored);
        Assert.False(blockedRun.Success);
        Assert.Contains("Pending rollback state", blockedRun.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, powerPlanManager.SetActivePlanCallCount);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_BlocksRetryWhenFailedRunHasUnresolvedRollback()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new IncompleteRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Blocked Retry Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var firstResult = await orchestrator.EvaluateForegroundAsync();
        var secondResult = await orchestrator.EvaluateForegroundAsync();

        Assert.False(firstResult.Success);
        Assert.False(secondResult.Success);
        Assert.Equal(1, powerPlanManager.SetActivePlanCallCount);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Contains("Pending rollback state", secondResult.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_BlocksPendingRollbackWhenNextForegroundHasNoMatchingProfile()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new IncompleteRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Matched Then Unmatched",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new SequenceForegroundWatcher(
                new ForegroundWindowSnapshot(1, 42, "Game Window"),
                new ForegroundWindowSnapshot(1, 43, "Browser Window")),
            new ProcessInspectorBySnapshot(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var failedRun = await orchestrator.EvaluateForegroundAsync();
        var blockedRun = await orchestrator.EvaluateForegroundAsync();

        Assert.False(failedRun.Success);
        Assert.False(blockedRun.Success);
        Assert.Contains("Pending rollback state", blockedRun.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, powerPlanManager.SetActivePlanCallCount);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_BlocksPendingRollbackWhenForegroundDisappears()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: false);
        var rollbackManager = new IncompleteRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Matched Then Missing",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new FirstThenMissingForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var failedRun = await orchestrator.EvaluateForegroundAsync();
        var blockedRun = await orchestrator.EvaluateForegroundAsync();

        Assert.False(failedRun.Success);
        Assert.False(blockedRun.Success);
        Assert.False(blockedRun.FailureRecoveryCompleted);
        Assert.Contains("could not be fully restored", blockedRun.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, powerPlanManager.SetActivePlanCallCount);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_BlocksWhenRollbackStateCannotBeRead()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: true);
        var profile = new PerformanceProfile
        {
            Name = "Unreadable State Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var logger = new RecordingLogger();
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            new UnreadableRollbackManager(),
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            logger);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.False(result.FailureRecoveryCompleted);
        Assert.Equal(0, powerPlanManager.SetActivePlanCallCount);
        Assert.Contains("could not be read", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logger.Messages, message => message.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RollsBackWhenActionExecutorThrowsAfterPartialApply()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Throwing Action Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            actionExecutor: new ThrowingActionExecutor(rollbackManager));

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.True(result.FailureRecoveryCompleted);
        Assert.Equal(1, result.ConsecutiveFailureCount);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Null(await rollbackManager.GetLastKnownStateAsync());
        Assert.Contains("Automatic rollback completed", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RollsBackBeforePropagatingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Cancelled Action Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            actionExecutor: new CancelingActionExecutor(rollbackManager, cancellation));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.EvaluateForegroundAsync(cancellation.Token));

        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Null(await rollbackManager.GetLastKnownStateAsync());
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RollsBackWhenCancellationArrivesAfterActionsComplete()
    {
        using var cancellation = new CancellationTokenSource();
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Cancelled Metrics Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new CancelingMetricsCollector(cancellation),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            actionExecutor: new CompletingActionExecutor(rollbackManager));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.EvaluateForegroundAsync(cancellation.Token));

        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Null(await rollbackManager.GetLastKnownStateAsync());
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RollsBackFailedActionsWithIndependentTokenBeforePropagatingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new CancellationAwareRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Cancelled Failed Action Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new NoMetricsCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            actionExecutor: new CancelingFailedActionExecutor(rollbackManager, cancellation));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            orchestrator.EvaluateForegroundAsync(cancellation.Token));

        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.False(rollbackManager.LastRollbackTokenWasCanceled);
        Assert.Null(await rollbackManager.GetLastKnownStateAsync());
    }

    [Fact]
    public async Task EvaluateForegroundAsync_KeepsActiveSessionWhenCurrentMetricsCollectionFails()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var logger = new RecordingLogger();
        var profile = new PerformanceProfile
        {
            Name = "Metrics Failure Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            new RecordingRollbackManager(),
            new ThrowingMetricsCollector(),
            new RecordingRuntimeStateStore(),
            logger);

        var firstResult = await orchestrator.EvaluateForegroundAsync();
        var secondResult = await orchestrator.EvaluateForegroundAsync();

        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        Assert.Null(secondResult.Metrics);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("active scheduling session", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RollsBackNewSessionWhenRuntimeHistoryPersistenceFails()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Runtime History Failure Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new ThrowingRuntimeStateStore(),
            new RecordingLogger(),
            actionExecutor: new CompletingActionExecutor(rollbackManager));

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.True(result.RuntimeStatePersistenceFailed);
        Assert.True(result.FailureRecoveryCompleted);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Null(await rollbackManager.GetLastKnownStateAsync());
    }

    [Fact]
    public async Task EvaluateForegroundAsync_DoesNotApplyAdvancedSettingsWithoutRollbackBaseline()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var powerPlanManager = new RecordingPowerPlanManager(activePlan, switchSucceeds: true)
        {
            AdvancedSettingsToReturn = null
        };
        var profile = new PerformanceProfile
        {
            Name = "Safe Advanced Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = activePlan.SchemeGuid,
                Advanced = new PowerPlanAdvancedPreference { ProcessorMaxStatePercent = 80 }
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            new RecordingRollbackManager(),
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.Equal(1, powerPlanManager.GetAdvancedSettingsCallCount);
        Assert.Equal(0, powerPlanManager.ApplyAdvancedSettingsCallCount);
        var action = Assert.Single(result.Actions, item =>
            item.Plan.Kind == SchedulerActionKind.AdvancedPowerSettings);
        Assert.Equal(SchedulerActionStatus.Failed, action.Status);
        Assert.Equal(SchedulerActionReasonCode.BaselineCaptureFailed, action.ReasonCode);
        Assert.False(action.Plan.RequiresRollback);
        Assert.Contains("original values", action.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_DoesNotSwitchPowerPlanWithoutRollbackBaseline()
    {
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(targetPlan, switchSucceeds: true)
        {
            ActivePlanToReturn = null
        };
        var profile = new PerformanceProfile
        {
            Name = "Power Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference { SchemeGuid = targetPlan.SchemeGuid }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(targetPlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            new StubPriorityManager(),
            new StubProfileManager(profile),
            new RecordingRollbackManager(),
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.False(powerPlanManager.SetActivePlanCalled);
        var action = Assert.Single(result.Actions, item =>
            item.Plan.Kind == SchedulerActionKind.PowerPlanSwitch);
        Assert.Equal(SchedulerActionStatus.Failed, action.Status);
        Assert.Equal(SchedulerActionReasonCode.BaselineCaptureFailed, action.ReasonCode);
        Assert.False(action.Plan.RequiresRollback);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_DoesNotApplyPriorityWithoutOriginalPriority()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var priorityManager = new MissingPriorityBaselineManager();
        var profile = new PerformanceProfile
        {
            Name = "Priority Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            Priority = new PriorityPreference { ForegroundPriority = PriorityLevel.High }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            new RecordingRollbackManager(),
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.False(result.Success);
        Assert.Equal(0, priorityManager.ApplyCallCount);
        var action = Assert.Single(result.Actions, item =>
            item.Plan.Kind == SchedulerActionKind.ForegroundPriority);
        Assert.Equal(SchedulerActionStatus.Failed, action.Status);
        Assert.Equal(SchedulerActionReasonCode.ProcessStateUnavailable, action.ReasonCode);
        Assert.False(action.Plan.RequiresRollback);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_DoesNotInvokeUnsupportedAdjustmentProviders()
    {
        var powerPlanManager = new RecordingPowerPlanManager(
            new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true),
            switchSucceeds: true);
        var priorityManager = new RecordingPriorityManager();
        var metricsCollector = new RecordingMetricsCollector();
        var profile = new PerformanceProfile
        {
            Name = "Unsupported Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = Guid.NewGuid(),
                Advanced = new PowerPlanAdvancedPreference { ProcessorMaxStatePercent = 80 }
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new UnsupportedCapabilityDetector(),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            powerPlanManager,
            priorityManager,
            new StubProfileManager(profile),
            new RecordingRollbackManager(),
            metricsCollector,
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.Equal(SchedulerActionStatus.Unsupported, result.PriorityChange?.Status);
        Assert.Contains("unsupported adjustments", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(powerPlanManager.SetActivePlanCalled);
        Assert.Equal(0, powerPlanManager.GetAdvancedSettingsCallCount);
        Assert.Equal(0, powerPlanManager.ApplyAdvancedSettingsCallCount);
        Assert.Equal(0, priorityManager.ProviderCallCount);
        Assert.Equal(0, metricsCollector.CallCount);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.PowerPlanSwitch &&
            action.Status == SchedulerActionStatus.Unsupported &&
            action.ReasonCode == SchedulerActionReasonCode.CapabilityUnavailable);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.AdvancedPowerSettings &&
            action.Status == SchedulerActionStatus.Unsupported &&
            action.ReasonCode == SchedulerActionReasonCode.CapabilityUnavailable);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.ForegroundPriority &&
            action.Status == SchedulerActionStatus.Unsupported &&
            action.ReasonCode == SchedulerActionReasonCode.CapabilityUnavailable);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RestoresPreviousSessionBeforeApplyingNextProfile()
    {
        var events = new List<string>();
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager(events);
        var priorityManager = new RecordingPriorityManager(events);
        var profiles = new[]
        {
            new PerformanceProfile
            {
                Id = "game-profile",
                Name = "Game Profile",
                Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
            },
            new PerformanceProfile
            {
                Id = "browser-profile",
                Name = "Browser Profile",
                Match = new ProfileMatchCriteria { ProcessNames = new[] { "browser" } }
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new SequenceForegroundWatcher(
                new ForegroundWindowSnapshot(1, 42, "Game Window"),
                new ForegroundWindowSnapshot(2, 43, "Browser Window")),
            new ProcessInspectorBySnapshot(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profiles),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var firstResult = await orchestrator.EvaluateForegroundAsync();
        var secondResult = await orchestrator.EvaluateForegroundAsync();

        Assert.Equal(new[] { "priority:42", "rollback", "priority:43" }, events);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.NotEqual(firstResult.TransactionId, secondResult.TransactionId);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RestoresActiveSessionWhenForegroundWindowDisappears()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Focus Loss Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
        };
        var orchestrator = new SchedulerOrchestrator(
            new FirstThenMissingForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new RecordingPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var appliedResult = await orchestrator.EvaluateForegroundAsync();
        var focusLostResult = await orchestrator.EvaluateForegroundAsync();

        Assert.True(appliedResult.Success);
        Assert.False(focusLostResult.Success);
        Assert.True(focusLostResult.FailureRecoveryCompleted);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Contains("restored", focusLostResult.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_RestoresActiveSessionWhenProcessInspectionFails()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Inspection Failure Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new FirstThenNullProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new RecordingPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var appliedResult = await orchestrator.EvaluateForegroundAsync();
        var inspectionFailureResult = await orchestrator.EvaluateForegroundAsync();

        Assert.True(appliedResult.Success);
        Assert.False(inspectionFailureResult.Success);
        Assert.True(inspectionFailureResult.FailureRecoveryCompleted);
        Assert.Equal(1, rollbackManager.RollbackCallCount);
        Assert.Contains("restored", inspectionFailureResult.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_ReusesTransactionForUnchangedSchedulingSession()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var profile = new PerformanceProfile
        {
            Name = "Stable Session",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            Priority = new PriorityPreference { ForegroundPriority = PriorityLevel.High }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var firstResult = await orchestrator.EvaluateForegroundAsync();
        var secondResult = await orchestrator.EvaluateForegroundAsync();

        Assert.NotNull(firstResult.TransactionId);
        Assert.Equal(firstResult.TransactionId, secondResult.TransactionId);
        Assert.NotEqual(firstResult.RunId, secondResult.RunId);
        Assert.Equal(firstResult.TransactionId, rollbackManager.LastCapturedState?.TransactionId);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_CapturesAndAppliesTheSamePreparedProcessPlan()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var priorityManager = new TransactionalPriorityManager();
        var profile = new PerformanceProfile
        {
            Name = "Transactional Profile",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true
            },
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "default",
                    TargetPriority = PriorityLevel.BelowNormal,
                    PreferEfficiencyMode = true
                }
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger());

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.Equal(1, priorityManager.CaptureCallCount);
        Assert.Equal(1, priorityManager.PreparedApplyCallCount);
        Assert.Equal(0, priorityManager.LegacyApplyCallCount);
        Assert.NotNull(rollbackManager.LastCapturedState);
        Assert.Equal(PriorityLevel.Normal, rollbackManager.LastCapturedState.OriginalPriorities[42]);
        Assert.Equal(PriorityLevel.AboveNormal, rollbackManager.LastCapturedState.AppliedPriorities[42]);
        Assert.Equal(PriorityLevel.Normal, rollbackManager.LastCapturedState.OriginalPriorities[50]);
        Assert.Equal(PriorityLevel.BelowNormal, rollbackManager.LastCapturedState.AppliedPriorities[50]);
        Assert.False(rollbackManager.LastCapturedState.OriginalEfficiencyModes[50].IsEnabled);
        Assert.True(rollbackManager.LastCapturedState.AppliedEfficiencyModes[50]);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.ForegroundPriority);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.BackgroundPriority &&
            action.Plan.Target.Contains("background", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.BackgroundEfficiencyMode &&
            action.Plan.Target.Contains("background", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateForegroundAsync_WhenProfileOptOutArbitration_AppliesAllBackgroundBaselinesNormally()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var startTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var baseline1 = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 101,
            Identity = new ProcessIdentitySnapshot { ProcessId = 101, ProcessName = "bg-1", StartedAt = startTime },
            ProcessName = "bg-1",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var baseline2 = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 102,
            Identity = new ProcessIdentitySnapshot { ProcessId = 102, ProcessName = "bg-2", StartedAt = startTime },
            ProcessName = "bg-2",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var priorityManager = new ArbitrationTestPriorityManager([baseline1, baseline2]);
        var arbitrationProvider = new StubArbitrationProvider(new ResourceArbitrationSummary
        {
            State = ResourceArbitrationState.RecommendBackgroundRelief,
            Recommendations = [new ResourceArbitrationRecommendation { ProcessId = 101, ProcessName = "bg-1", StartedAt = startTime }]
        });
        var profile = new PerformanceProfile
        {
            Name = "Non-Arbitrated Profile",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true,
                UseResourceArbitration = false
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            resourceArbitrationProvider: arbitrationProvider);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.Null(result.ResourceArbitration);
        Assert.Equal(0, arbitrationProvider.CallCount);
        Assert.Equal([101, 102], priorityManager.AppliedBackgroundProcessIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task EvaluateForegroundAsync_WhenProfileOptInArbitration_AndProviderMissing_BlocksAllBackgroundBaselines()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var startTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var baseline = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 101,
            Identity = new ProcessIdentitySnapshot { ProcessId = 101, ProcessName = "bg-1", StartedAt = startTime },
            ProcessName = "bg-1",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var priorityManager = new ArbitrationTestPriorityManager([baseline]);
        var profile = new PerformanceProfile
        {
            Name = "Arbitrated Profile",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true,
                UseResourceArbitration = true
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            resourceArbitrationProvider: null);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.Null(result.ResourceArbitration);
        Assert.Empty(priorityManager.AppliedBackgroundProcessIds);
        Assert.Empty(result.BackgroundAdjustments);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_WhenProfileOptInArbitration_AppliesOnlyRecommendedCandidatesMatchingStartTime()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var startTime1 = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var startTime2 = new DateTimeOffset(2026, 10, 8, 12, 30, 0, TimeSpan.Zero);
        var baseline1 = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 101,
            Identity = new ProcessIdentitySnapshot { ProcessId = 101, ProcessName = "bg-1", StartedAt = startTime1 },
            ProcessName = "bg-1",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var baseline2 = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 102,
            Identity = new ProcessIdentitySnapshot { ProcessId = 102, ProcessName = "bg-2", StartedAt = startTime2 },
            ProcessName = "bg-2",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var priorityManager = new ArbitrationTestPriorityManager([baseline1, baseline2]);
        var arbitrationProvider = new StubArbitrationProvider(new ResourceArbitrationSummary
        {
            State = ResourceArbitrationState.RecommendBackgroundRelief,
            Recommendations = [new ResourceArbitrationRecommendation { ProcessId = 101, ProcessName = "bg-1", StartedAt = startTime1 }]
        });
        var profile = new PerformanceProfile
        {
            Name = "Arbitrated Profile",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true,
                UseResourceArbitration = true
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            resourceArbitrationProvider: arbitrationProvider);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.ResourceArbitration);
        Assert.Equal(ResourceArbitrationState.RecommendBackgroundRelief, result.ResourceArbitration.State);
        Assert.Equal(1, arbitrationProvider.CallCount);
        Assert.Equal([101], priorityManager.AppliedBackgroundProcessIds);
        Assert.NotNull(rollbackManager.LastCapturedState);
        Assert.True(rollbackManager.LastCapturedState.OriginalPriorities.ContainsKey(101));
        Assert.False(rollbackManager.LastCapturedState.OriginalPriorities.ContainsKey(102));
    }

    [Fact]
    public async Task EvaluateForegroundAsync_WhenCandidateStartedAtMismatches_BlocksRecycledProcess()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var liveStartTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var staleRecommendationStartTime = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var baseline = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 101,
            Identity = new ProcessIdentitySnapshot { ProcessId = 101, ProcessName = "bg-1", StartedAt = liveStartTime },
            ProcessName = "bg-1",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var priorityManager = new ArbitrationTestPriorityManager([baseline]);
        var arbitrationProvider = new StubArbitrationProvider(new ResourceArbitrationSummary
        {
            State = ResourceArbitrationState.RecommendBackgroundRelief,
            Recommendations = [new ResourceArbitrationRecommendation { ProcessId = 101, ProcessName = "bg-1", StartedAt = staleRecommendationStartTime }]
        });
        var profile = new PerformanceProfile
        {
            Name = "Arbitrated Profile",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true,
                UseResourceArbitration = true
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            resourceArbitrationProvider: arbitrationProvider);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.Empty(priorityManager.AppliedBackgroundProcessIds);
        Assert.Empty(result.BackgroundAdjustments);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_WhenArbitrationProviderThrows_SafelyFallsBackToMissingEvidenceBlocking()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var startTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var baseline = new BackgroundProcessPriorityBaseline
        {
            ProcessId = 101,
            Identity = new ProcessIdentitySnapshot { ProcessId = 101, ProcessName = "bg-1", StartedAt = startTime },
            ProcessName = "bg-1",
            Category = "Default",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetEfficiencyMode = true
        };
        var priorityManager = new ArbitrationTestPriorityManager([baseline]);
        var arbitrationProvider = new StubArbitrationProvider(null, new InvalidOperationException("Simulated provider failure"));
        var logger = new RecordingLogger();
        var profile = new PerformanceProfile
        {
            Name = "Arbitrated Profile",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true,
                UseResourceArbitration = true
            }
        };
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            priorityManager,
            new StubProfileManager(profile),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            logger,
            resourceArbitrationProvider: arbitrationProvider);

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.Null(result.ResourceArbitration);
        Assert.Empty(priorityManager.AppliedBackgroundProcessIds);
        Assert.Contains(logger.Messages, msg => msg.Contains("Resource arbitration evaluation failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdaptTargetExperienceAsync_WhenNoActiveSession_ReturnsNull()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var coordinator = new StubTargetExperienceAdaptiveCoordinator();
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager([]),
            new RecordingRollbackManager(),
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            targetExperienceAdaptiveCoordinator: coordinator);

        var decision = new TargetExperienceControlDecision();
        var result = await orchestrator.AdaptTargetExperienceAsync(decision);

        Assert.Null(result);
        Assert.Equal(0, coordinator.EvaluateCallCount);
    }

    [Fact]
    public async Task AdaptTargetExperienceAsync_WithActiveSession_DelegatesToCoordinator()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var profile = new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] },
            TargetExperience = new TargetExperiencePreference
            {
                Enabled = true,
                ControlMode = TargetExperienceControlMode.Automatic
            }
        };
        var coordinator = new StubTargetExperienceAdaptiveCoordinator();
        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan, activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(profile),
            new RecordingRollbackManager(),
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            targetExperienceAdaptiveCoordinator: coordinator);

        var runResult = await orchestrator.EvaluateForegroundAsync();
        Assert.True(runResult.Success);

        var decision = new TargetExperienceControlDecision
        {
            State = TargetExperienceControlState.NeedsMorePerformance,
            IsStable = true,
            Recommendation = new TargetExperienceAdjustmentRecommendation
            {
                Kind = TargetExperienceAdjustmentKind.IncreasePerformance,
                CanApplyAutomatically = true
            }
        };
        var adaptResult = await orchestrator.AdaptTargetExperienceAsync(decision);

        Assert.NotNull(adaptResult);
        Assert.True(adaptResult!.Applied);
        Assert.Equal(1, coordinator.EvaluateCallCount);
    }

    [Fact]
    public async Task EvaluateForegroundAsync_UsesRuntimeDeviceStrategyProfiles()
    {
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var rollbackManager = new RecordingRollbackManager();
        var package = new DeviceStrategyPackage
        {
            PackageId = "runtime-package",
            Name = "Runtime Package"
        };
        var packageProfile = new PerformanceProfile
        {
            Id = "package-game",
            Name = "Package Game",
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] }
        };
        var selection = new DeviceStrategyRuntimeSelection
        {
            Profiles = [packageProfile],
            SelectedPackage = package,
            SelectedCompatibility = new DeviceCompatibilityResult
            {
                Level = DeviceCompatibilityLevel.SameHardware
            },
            PackageProfileIds = new HashSet<string>([packageProfile.Id], StringComparer.OrdinalIgnoreCase),
            SelectionReasonKey = "DeviceStrategyRuntime.SelectedSameHardware"
        };

        var orchestrator = new SchedulerOrchestrator(
            new StubForegroundWatcher(),
            new StubProcessInspector(),
            new StubCapabilityDetector(activePlan),
            new StubPowerStatusProvider(PowerSourceMode.Ac),
            new RecordingPowerPlanManager(activePlan, switchSucceeds: true),
            new StubPriorityManager(),
            new StubProfileManager(new PerformanceProfile { Id = "local-default", IsGlobalDefault = true }),
            rollbackManager,
            new StubMetricsCollector(),
            new RecordingRuntimeStateStore(),
            new RecordingLogger(),
            actionExecutor: new CompletingActionExecutor(rollbackManager),
            runtimeProfileResolver: new StubRuntimeProfileResolver(selection));

        var result = await orchestrator.EvaluateForegroundAsync();

        Assert.True(result.Success);
        Assert.Equal("package-game", result.MatchResult?.Profile.Id);
        Assert.Equal("runtime-package", result.DeviceStrategySelection?.SelectedPackage?.PackageId);
    }

    private sealed class StubForegroundWatcher : IForegroundWatcher
    {
        public Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ForegroundWindowSnapshot?>(new ForegroundWindowSnapshot(1, 42, "Game Window"));
    }

    private sealed class FirstThenMissingForegroundWatcher : IForegroundWatcher
    {
        private int _callCount;

        public Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult<ForegroundWindowSnapshot?>(
                _callCount == 1 ? new ForegroundWindowSnapshot(1, 42, "Game Window") : null);
        }
    }

    private sealed class SequenceForegroundWatcher(params ForegroundWindowSnapshot[] snapshots) : IForegroundWatcher
    {
        private int _index;

        public Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = snapshots[Math.Min(_index, snapshots.Length - 1)];
            _index++;
            return Task.FromResult<ForegroundWindowSnapshot?>(snapshot);
        }
    }

    private sealed class StubProcessInspector : IProcessInspector
    {
        public Task<FocusedAppContext?> InspectAsync(
            ForegroundWindowSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FocusedAppContext?>(new FocusedAppContext
            {
                ProcessId = snapshot.ProcessId,
                ProcessName = "game",
                WindowTitle = snapshot.WindowTitle,
                WindowHandle = snapshot.WindowHandle,
                Classification = ProcessClassification.Game
            });
    }

    private sealed class FirstThenNullProcessInspector : IProcessInspector
    {
        private int _callCount;

        public Task<FocusedAppContext?> InspectAsync(
            ForegroundWindowSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult<FocusedAppContext?>(_callCount == 1
                ? new FocusedAppContext
                {
                    ProcessId = snapshot.ProcessId,
                    ProcessName = "game",
                    WindowTitle = snapshot.WindowTitle,
                    WindowHandle = snapshot.WindowHandle,
                    Classification = ProcessClassification.Game
                }
                : null);
        }
    }

    private sealed class ProcessInspectorBySnapshot : IProcessInspector
    {
        public Task<FocusedAppContext?> InspectAsync(
            ForegroundWindowSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FocusedAppContext?>(new FocusedAppContext
            {
                ProcessId = snapshot.ProcessId,
                ProcessName = snapshot.ProcessId == 42 ? "game" : "browser",
                WindowTitle = snapshot.WindowTitle,
                WindowHandle = snapshot.WindowHandle
            });
    }

    private sealed class StubCapabilityDetector(params PowerPlanInfo[] plans) : ICapabilityDetector
    {
        public Task<CapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapabilitySnapshot
            {
                SupportsPowerPlanSwitching = true,
                SupportsPriorityBoost = true,
                SupportsRollback = true,
                SupportsMetricsCollection = true,
                SupportsEfficiencyModeHint = true,
                AvailablePowerPlans = plans
            });
    }

    private sealed class UnsupportedCapabilityDetector : ICapabilityDetector
    {
        public Task<CapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapabilitySnapshot
            {
                SupportsRollback = true,
                SupportsPowerPlanSwitching = false,
                SupportsPriorityBoost = false,
                SupportsMetricsCollection = false
            });
    }

    private sealed class StubPowerStatusProvider(PowerSourceMode powerSourceMode) : IPowerStatusProvider
    {
        public Task<PowerSourceMode> GetCurrentPowerSourceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(powerSourceMode);
    }

    private sealed class RecordingPowerPlanManager : IPowerPlanManager
    {
        private readonly PowerPlanInfo _configuredPlan;
        private readonly bool _switchSucceeds;

        public RecordingPowerPlanManager(PowerPlanInfo activePlan, bool switchSucceeds)
        {
            _configuredPlan = activePlan;
            _switchSucceeds = switchSucceeds;
            ActivePlanToReturn = activePlan;
            AdvancedSettingsToReturn = new PowerPlanAdvancedState
            {
                SchemeGuid = activePlan.SchemeGuid,
                PowerSourceMode = PowerSourceMode.Ac,
                ProcessorMaxStatePercent = 100
            };
        }

        public bool SetActivePlanCalled { get; private set; }

        public int SetActivePlanCallCount { get; private set; }

        public int GetAdvancedSettingsCallCount { get; private set; }

        public int ApplyAdvancedSettingsCallCount { get; private set; }

        public PowerPlanAdvancedState? AdvancedSettingsToReturn { get; set; }

        public PowerPlanInfo? ActivePlanToReturn { get; set; }

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>(new[] { _configuredPlan });

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ActivePlanToReturn);

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default)
        {
            SetActivePlanCalled = true;
            SetActivePlanCallCount++;
            return Task.FromResult(_switchSucceeds);
        }

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default)
        {
            GetAdvancedSettingsCallCount++;
            return Task.FromResult(AdvancedSettingsToReturn);
        }

        public Task<bool> ApplyAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            PowerPlanAdvancedPreference preference,
            CancellationToken cancellationToken = default)
        {
            ApplyAdvancedSettingsCallCount++;
            return Task.FromResult(true);
        }

        public Task<bool> RestoreAdvancedSettingsAsync(
            PowerPlanAdvancedState state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubPriorityManager : IPriorityManager
    {
        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessIdentitySnapshot?>(CreateIdentity(processId));

        public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(PriorityLevel.AboveNormal);

        public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = profile.Priority.ForegroundPriority,
                AppliedPriority = profile.Priority.ForegroundPriority,
                Status = SchedulerActionStatus.Skipped
            });

        public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(Array.Empty<BackgroundProcessPriorityBaseline>());

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(Array.Empty<BackgroundProcessAdjustmentResult>());
    }

    private sealed class RecordingPriorityManager(List<string>? events = null) : IPriorityManager
    {
        public int ProviderCallCount { get; private set; }

        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            ProviderCallCount++;
            return Task.FromResult<ProcessIdentitySnapshot?>(CreateIdentity(processId));
        }

        public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default)
        {
            ProviderCallCount++;
            return Task.FromResult<PriorityLevel?>(PriorityLevel.Normal);
        }

        public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default)
        {
            ProviderCallCount++;
            return Task.FromResult(true);
        }

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            ProviderCallCount++;
            events?.Add($"priority:{app.ProcessId}");
            return Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = PriorityLevel.Normal,
                AppliedPriority = profile.Priority.ForegroundPriority,
                Status = SchedulerActionStatus.Applied
            });
        }

        public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            ProviderCallCount++;
            return Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(
                Array.Empty<BackgroundProcessPriorityBaseline>());
        }

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            ProviderCallCount++;
            return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(
                Array.Empty<BackgroundProcessAdjustmentResult>());
        }
    }

    private sealed class NoMetricsCapabilityDetector(params PowerPlanInfo[] plans) : ICapabilityDetector
    {
        public Task<CapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapabilitySnapshot
            {
                SupportsPowerPlanSwitching = true,
                SupportsPriorityBoost = true,
                SupportsRollback = true,
                SupportsMetricsCollection = false,
                SupportsEfficiencyModeHint = true,
                AvailablePowerPlans = plans
            });
    }

    private sealed class MissingPriorityBaselineManager : IPriorityManager
    {
        public int ApplyCallCount { get; private set; }

        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessIdentitySnapshot?>(CreateIdentity(processId));

        public Task<PriorityLevel?> TryGetPriorityAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(null);

        public Task<bool> SetPriorityAsync(
            int processId,
            PriorityLevel priority,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            return Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                Status = SchedulerActionStatus.Applied
            });
        }

        public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(
                Array.Empty<BackgroundProcessPriorityBaseline>());

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(
                Array.Empty<BackgroundProcessAdjustmentResult>());
    }

    private sealed class TransactionalPriorityManager : IPriorityManager
    {
        private readonly IReadOnlyList<BackgroundProcessPriorityBaseline> _baselines =
        [
            new BackgroundProcessPriorityBaseline
            {
                ProcessId = 50,
                Identity = CreateIdentity(50),
                ProcessName = "background",
                Category = "Default",
                PreviousPriority = PriorityLevel.Normal,
                TargetPriority = PriorityLevel.BelowNormal,
                PreviousEfficiencyMode = new ProcessEfficiencyModeState { IsEnabled = false },
                TargetEfficiencyMode = true
            }
        ];

        public int CaptureCallCount { get; private set; }

        public int PreparedApplyCallCount { get; private set; }

        public int LegacyApplyCallCount { get; private set; }

        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessIdentitySnapshot?>(CreateIdentity(processId));

        public Task<PriorityLevel?> TryGetPriorityAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(PriorityLevel.Normal);

        public Task<bool> SetPriorityAsync(
            int processId,
            PriorityLevel priority,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = PriorityLevel.Normal,
                AppliedPriority = profile.Priority.ForegroundPriority,
                Status = SchedulerActionStatus.Applied
            });

        public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            CaptureCallCount++;
            return Task.FromResult(_baselines);
        }

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            LegacyApplyCallCount++;
            return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(
                Array.Empty<BackgroundProcessAdjustmentResult>());
        }

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            IReadOnlyList<BackgroundProcessPriorityBaseline> preparedBaselines,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(_baselines, preparedBaselines);
            PreparedApplyCallCount++;
            return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(
                _baselines.Select(baseline => new BackgroundProcessAdjustmentResult
                {
                    ProcessId = baseline.ProcessId,
                    ProcessName = baseline.ProcessName,
                    Category = baseline.Category,
                    PreviousPriority = baseline.PreviousPriority,
                    AppliedPriority = baseline.TargetPriority,
                    PreviousEfficiencyMode = baseline.PreviousEfficiencyMode?.IsEnabled,
                    AppliedEfficiencyMode = baseline.TargetEfficiencyMode,
                    Status = SchedulerActionStatus.Applied
                }).ToArray());
        }
    }

    private static ProcessIdentitySnapshot CreateIdentity(int processId) =>
        new()
        {
            ProcessId = processId,
            ProcessName = $"process-{processId}",
            StartedAt = DateTimeOffset.UnixEpoch
        };

    private sealed class StubProfileManager(params PerformanceProfile[] profiles) : IProfileManager
    {
        public Task<IReadOnlyList<PerformanceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PerformanceProfile>>(profiles);

        public Task SaveProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ProfileRevisionInfo>> LoadProfileHistoryAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileRevisionInfo>>(Array.Empty<ProfileRevisionInfo>());

        public Task<PerformanceProfile?> RollbackProfileAsync(string profileId, string revisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PerformanceProfile?>(null);

        public Task ExportProfilesAsync(string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ImportProfilesAsync(string sourcePath, bool overwriteExisting, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ConfigurationStoreSnapshot> CaptureImportSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationStoreSnapshot());

        public Task RestoreImportSnapshotAsync(ConfigurationStoreSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubRuntimeProfileResolver(DeviceStrategyRuntimeSelection selection) : IRuntimeProfileResolver
    {
        public Task<DeviceStrategyRuntimeSelection> ResolveAsync(
            IReadOnlyList<PerformanceProfile> localProfiles,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(selection with { PowerSourceMode = powerSourceMode });
    }

    private sealed class RecordingRollbackManager(List<string>? events = null) : IRollbackManager
    {
        private SchedulerState? _pendingState;

        public SchedulerState? LastCapturedState { get; private set; }

        public int RollbackCallCount { get; private set; }

        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
        {
            LastCapturedState = state;
            _pendingState = state;
            return Task.CompletedTask;
        }

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_pendingState);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCallCount++;
            events?.Add("rollback");
            _pendingState = null;
            return Task.CompletedTask;
        }
    }

    private sealed class IncompleteRollbackManager : IRollbackManager
    {
        private SchedulerState? _pendingState;

        public int RollbackCallCount { get; private set; }

        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
        {
            _pendingState = state;
            return Task.CompletedTask;
        }

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_pendingState);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationAwareRollbackManager : IRollbackManager
    {
        private SchedulerState? _pendingState;

        public int RollbackCallCount { get; private set; }

        public bool LastRollbackTokenWasCanceled { get; private set; }

        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
        {
            _pendingState = state;
            return Task.CompletedTask;
        }

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_pendingState);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCallCount++;
            LastRollbackTokenWasCanceled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            _pendingState = null;
            return Task.CompletedTask;
        }
    }

    private sealed class UnreadableRollbackManager : IRollbackManager
    {
        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            throw new RollbackStateUnavailableException(
                "rollback-state.json",
                new InvalidOperationException("invalid rollback state"));

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingActionExecutor(IRollbackManager rollbackManager) : ISchedulerActionExecutor
    {
        public async Task<SchedulerActionExecutionResult> ExecuteAsync(
            SchedulerActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            await rollbackManager.CaptureAsync(new SchedulerState
            {
                TransactionId = request.TransactionId
            }, cancellationToken);
            throw new InvalidOperationException("provider failed after partial apply");
        }
    }

    private sealed class CancelingActionExecutor(
        IRollbackManager rollbackManager,
        CancellationTokenSource cancellation) : ISchedulerActionExecutor
    {
        public async Task<SchedulerActionExecutionResult> ExecuteAsync(
            SchedulerActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            await rollbackManager.CaptureAsync(new SchedulerState
            {
                TransactionId = request.TransactionId
            }, cancellationToken);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CompletingActionExecutor(IRollbackManager rollbackManager) : ISchedulerActionExecutor
    {
        public async Task<SchedulerActionExecutionResult> ExecuteAsync(
            SchedulerActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            await rollbackManager.CaptureAsync(new SchedulerState
            {
                TransactionId = request.TransactionId
            }, cancellationToken);
            return new SchedulerActionExecutionResult();
        }
    }

    private sealed class CancelingFailedActionExecutor(
        IRollbackManager rollbackManager,
        CancellationTokenSource cancellation) : ISchedulerActionExecutor
    {
        public async Task<SchedulerActionExecutionResult> ExecuteAsync(
            SchedulerActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            await rollbackManager.CaptureAsync(new SchedulerState
            {
                TransactionId = request.TransactionId
            }, cancellationToken);
            cancellation.Cancel();
            return new SchedulerActionExecutionResult
            {
                Actions =
                [
                    new SchedulerActionResult
                    {
                        Plan = new SchedulerActionPlan { Kind = SchedulerActionKind.ForegroundPriority },
                        Status = SchedulerActionStatus.Failed,
                        Message = "Simulated provider failure."
                    }
                ]
            };
        }
    }

    private sealed class CancelingMetricsCollector(CancellationTokenSource cancellation) : IMetricsCollector
    {
        public Task<MetricsSnapshot> CollectAsync(
            FocusedAppContext app,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(new MetricsSnapshot());
        }
    }

    private sealed class StubMetricsCollector : IMetricsCollector
    {
        public Task<MetricsSnapshot> CollectAsync(FocusedAppContext app, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetricsSnapshot());
    }

    private sealed class RecordingMetricsCollector : IMetricsCollector
    {
        public int CallCount { get; private set; }

        public Task<MetricsSnapshot> CollectAsync(FocusedAppContext app, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new MetricsSnapshot());
        }
    }

    private sealed class ThrowingMetricsCollector : IMetricsCollector
    {
        public Task<MetricsSnapshot> CollectAsync(
            FocusedAppContext app,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated metrics provider failure.");
    }

    private sealed class RecordingRuntimeStateStore : IRuntimeStateStore
    {
        public SchedulerRunResult? LastResult { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default)
        {
            LastResult = result;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SchedulerRunHistoryEntry>> LoadRecentRunsAsync(
            int count = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerRunHistoryEntry>>(
                Array.Empty<SchedulerRunHistoryEntry>());
    }

    private sealed class ThrowingRuntimeStateStore : IRuntimeStateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated runtime history persistence failure.");

        public Task<IReadOnlyList<SchedulerRunHistoryEntry>> LoadRecentRunsAsync(
            int count = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerRunHistoryEntry>>(
                Array.Empty<SchedulerRunHistoryEntry>());
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Messages { get; } = new();

        public event EventHandler<LogEntry>? EntryWritten;

        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) => Array.Empty<LogEntry>();

        public void Trace(string message) => Write(message);

        public void Info(string message) => Write(message);

        public void Warn(string message) => Write(message);

        public void Error(string message, Exception? exception = null) => Write(message);

        private void Write(string message)
        {
            Messages.Add(message);
            EntryWritten?.Invoke(this, new LogEntry(DateTimeOffset.UtcNow, AppLogLevel.Information, message));
        }
    }

    private sealed class ArbitrationTestPriorityManager(
        IReadOnlyList<BackgroundProcessPriorityBaseline> baselines,
        Dictionary<int, ProcessIdentitySnapshot>? identities = null) : IPriorityManager
    {
        private readonly IReadOnlyList<BackgroundProcessPriorityBaseline> _baselines = baselines;
        private readonly Dictionary<int, ProcessIdentitySnapshot> _identities = identities ?? [];
        public List<int> AppliedBackgroundProcessIds { get; } = [];

        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            if (_identities.TryGetValue(processId, out var snapshot))
            {
                return Task.FromResult<ProcessIdentitySnapshot?>(snapshot);
            }

            return Task.FromResult<ProcessIdentitySnapshot?>(CreateIdentity(processId));
        }

        public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(PriorityLevel.Normal);

        public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = PriorityLevel.Normal,
                AppliedPriority = profile.Priority.ForegroundPriority,
                Status = SchedulerActionStatus.Applied
            });

        public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_baselines);

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(Array.Empty<BackgroundProcessAdjustmentResult>());

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            IReadOnlyList<BackgroundProcessPriorityBaseline> preparedBaselines,
            CancellationToken cancellationToken = default)
        {
            var results = new List<BackgroundProcessAdjustmentResult>();
            foreach (var baseline in preparedBaselines)
            {
                AppliedBackgroundProcessIds.Add(baseline.ProcessId);
                results.Add(new BackgroundProcessAdjustmentResult
                {
                    ProcessId = baseline.ProcessId,
                    ProcessName = baseline.ProcessName,
                    Category = baseline.Category,
                    PreviousPriority = baseline.PreviousPriority,
                    AppliedPriority = baseline.TargetPriority,
                    PreviousEfficiencyMode = baseline.PreviousEfficiencyMode?.IsEnabled,
                    AppliedEfficiencyMode = baseline.TargetEfficiencyMode,
                    Status = SchedulerActionStatus.Applied
                });
            }
            return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(results);
        }
    }

    private sealed class StubArbitrationProvider(
        ResourceArbitrationSummary? summary,
        Exception? exceptionToThrow = null) : IResourceArbitrationProvider
    {
        public int CallCount { get; private set; }

        public Task<ResourceArbitrationSummary?> GetArbitrationSummaryAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (exceptionToThrow is not null)
            {
                throw exceptionToThrow;
            }
            return Task.FromResult(summary);
        }
    }

    private sealed class StubTargetExperienceAdaptiveCoordinator : ITargetExperienceAdaptiveCoordinator
    {
        public int EvaluateCallCount { get; private set; }
        public int ResetCallCount { get; private set; }

        public Task<TargetExperienceAdaptiveResult?> EvaluateAndApplyAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CapabilitySnapshot capabilities,
            PowerSourceMode powerSourceMode,
            PowerPlanInfo? activePowerPlan,
            TargetExperienceControlDecision decision,
            CancellationToken cancellationToken = default)
        {
            EvaluateCallCount++;
            return Task.FromResult<TargetExperienceAdaptiveResult?>(new TargetExperienceAdaptiveResult
            {
                Applied = true,
                AppliedProcessorMaxPercent = 85,
                ReasonKey = "TargetExperience.AdaptiveApplied"
            });
        }

        public Task ResetSessionAsync(CancellationToken cancellationToken = default)
        {
            ResetCallCount++;
            return Task.CompletedTask;
        }
    }
}
