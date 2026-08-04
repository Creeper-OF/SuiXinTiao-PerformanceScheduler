using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

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

    private sealed class StubForegroundWatcher : IForegroundWatcher
    {
        public Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ForegroundWindowSnapshot?>(new ForegroundWindowSnapshot(1, 42, "Game Window"));
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
}
