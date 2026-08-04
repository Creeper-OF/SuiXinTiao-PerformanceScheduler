using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class SchedulerActionExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CapturesRollbackStateBeforeApplyingPlatformActions()
    {
        var events = new List<string>();
        var originalPlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var targetPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var powerPlanManager = new RecordingPowerPlanManager(events, targetPlan);
        var priorityManager = new RecordingPriorityManager(events);
        var rollbackManager = new RecordingRollbackManager(events);
        var executor = new SchedulerActionExecutor(
            powerPlanManager,
            priorityManager,
            rollbackManager,
            new NullLogger());
        var profile = new PerformanceProfile
        {
            Id = "executor-test",
            Name = "Executor Test",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = targetPlan.SchemeGuid,
                PreferredPlanName = targetPlan.Name,
                RestoreOnExit = true
            },
            Priority = new PriorityPreference { ForegroundPriority = PriorityLevel.High }
        };

        var result = await executor.ExecuteAsync(new SchedulerActionExecutionRequest
        {
            TransactionId = Guid.NewGuid(),
            ActiveApp = CreateApp(),
            Match = new ProfileMatchResult(profile, 100, "test"),
            Capabilities = new CapabilitySnapshot
            {
                SupportsRollback = true,
                SupportsPowerPlanSwitching = true,
                SupportsPriorityBoost = true
            },
            PowerSourceMode = PowerSourceMode.Ac,
            ActivePowerPlan = originalPlan
        });

        Assert.Equal(new[] { "capture", "power", "priority" }, events);
        Assert.Equal(targetPlan.SchemeGuid, result.ActivePowerPlan?.SchemeGuid);
        Assert.Equal(SchedulerActionStatus.Applied, result.PriorityChange?.Status);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.PowerPlanSwitch &&
            action.Status == SchedulerActionStatus.Applied);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.ForegroundPriority &&
            action.Status == SchedulerActionStatus.Applied);
        Assert.Equal(originalPlan.SchemeGuid, rollbackManager.CapturedState?.PreviousPowerPlan?.SchemeGuid);
        Assert.Equal(PriorityLevel.Normal, rollbackManager.CapturedState?.OriginalPriorities[42]);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesAdvancedPowerBaselineBeforeApplyingSettings()
    {
        var events = new List<string>();
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var originalAdvancedState = new PowerPlanAdvancedState
        {
            SchemeGuid = activePlan.SchemeGuid,
            PowerSourceMode = PowerSourceMode.Ac,
            ProcessorMaxStatePercent = 100
        };
        var powerPlanManager = new RecordingPowerPlanManager(
            events,
            activePlan,
            originalAdvancedState);
        var rollbackManager = new RecordingRollbackManager(events);
        var executor = new SchedulerActionExecutor(
            powerPlanManager,
            new RecordingPriorityManager(events),
            rollbackManager,
            new NullLogger());
        var profile = new PerformanceProfile
        {
            Id = "advanced-test",
            Name = "Advanced Test",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = activePlan.SchemeGuid,
                Advanced = new PowerPlanAdvancedPreference { ProcessorMaxStatePercent = 80 }
            }
        };

        var result = await executor.ExecuteAsync(new SchedulerActionExecutionRequest
        {
            TransactionId = Guid.NewGuid(),
            ActiveApp = CreateApp(),
            Match = new ProfileMatchResult(profile, 100, "test"),
            Capabilities = new CapabilitySnapshot
            {
                SupportsRollback = true,
                SupportsPowerPlanSwitching = true,
                SupportsPriorityBoost = true
            },
            PowerSourceMode = PowerSourceMode.Ac,
            ActivePowerPlan = activePlan
        });

        Assert.Equal(new[] { "capture", "capture", "advanced", "priority" }, events);
        Assert.Equal(100, rollbackManager.CapturedState?.PreviousAdvancedPowerSettings?.ProcessorMaxStatePercent);
        Assert.Equal(80, rollbackManager.CapturedState?.AppliedAdvancedPowerSettings?.ProcessorMaxStatePercent);
        Assert.Contains(result.Actions, action =>
            action.Plan.Kind == SchedulerActionKind.AdvancedPowerSettings &&
            action.Status == SchedulerActionStatus.Applied);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsSafetyPolicyWhenProcessProviderRejectsAction()
    {
        var events = new List<string>();
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: true);
        var executor = new SchedulerActionExecutor(
            new RecordingPowerPlanManager(events, activePlan),
            new RecordingPriorityManager(events, SchedulerActionStatus.Unsupported),
            new RecordingRollbackManager(events),
            new NullLogger());
        var profile = new PerformanceProfile
        {
            Id = "safety-policy-test",
            Name = "Safety Policy Test",
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } },
            Priority = new PriorityPreference { ForegroundPriority = PriorityLevel.High }
        };

        var result = await executor.ExecuteAsync(new SchedulerActionExecutionRequest
        {
            TransactionId = Guid.NewGuid(),
            ActiveApp = CreateApp(),
            Match = new ProfileMatchResult(profile, 100, "test"),
            Capabilities = new CapabilitySnapshot
            {
                SupportsRollback = true,
                SupportsPriorityBoost = true
            },
            PowerSourceMode = PowerSourceMode.Ac,
            ActivePowerPlan = activePlan
        });

        var action = Assert.Single(result.Actions, candidate =>
            candidate.Plan.Kind == SchedulerActionKind.ForegroundPriority);
        Assert.Equal(SchedulerActionStatus.Unsupported, action.Status);
        Assert.Equal(SchedulerActionReasonCode.SafetyPolicyBlocked, action.ReasonCode);
    }

    private static FocusedAppContext CreateApp() => new()
    {
        ProcessId = 42,
        ProcessName = "game",
        WindowTitle = "Game",
        WindowHandle = 1
    };

    private sealed class RecordingPowerPlanManager(
        List<string> events,
        PowerPlanInfo targetPlan,
        PowerPlanAdvancedState? advancedState = null) : IPowerPlanManager
    {
        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>(new[] { targetPlan });

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanInfo?>(targetPlan with { IsActive = true });

        public Task<bool> SetActivePlanAsync(
            Guid schemeGuid,
            CancellationToken cancellationToken = default)
        {
            events.Add("power");
            return Task.FromResult(schemeGuid == targetPlan.SchemeGuid);
        }

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(advancedState);

        public Task<bool> ApplyAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            PowerPlanAdvancedPreference preference,
            CancellationToken cancellationToken = default)
        {
            events.Add("advanced");
            return Task.FromResult(advancedState is not null);
        }

        public Task<bool> RestoreAdvancedSettingsAsync(
            PowerPlanAdvancedState state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingPriorityManager(
        List<string> events,
        SchedulerActionStatus foregroundStatus = SchedulerActionStatus.Applied) : IPriorityManager
    {
        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessIdentitySnapshot?>(new ProcessIdentitySnapshot
            {
                ProcessId = processId,
                ProcessName = "game",
                StartedAt = DateTimeOffset.UnixEpoch
            });

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
            ApplyForegroundBoostAsync(
                app,
                profile,
                new ProcessIdentitySnapshot
                {
                    ProcessId = app.ProcessId,
                    ProcessName = app.ProcessName,
                    StartedAt = DateTimeOffset.UnixEpoch
                },
                cancellationToken);

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            ProcessIdentitySnapshot expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            events.Add("priority");
            return Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = PriorityLevel.Normal,
                AppliedPriority = foregroundStatus == SchedulerActionStatus.Applied
                    ? profile.Priority.ForegroundPriority
                    : PriorityLevel.Normal,
                Status = foregroundStatus,
                Message = foregroundStatus == SchedulerActionStatus.Unsupported
                    ? "Blocked by process safety policy."
                    : "Priority applied."
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

    private sealed class RecordingRollbackManager(List<string> events) : IRollbackManager
    {
        public SchedulerState? CapturedState { get; private set; }

        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
        {
            CapturedState = state;
            events.Add("capture");
            return Task.CompletedTask;
        }

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CapturedState);

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntry>? EntryWritten
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) => Array.Empty<LogEntry>();

        public void Trace(string message) { }

        public void Info(string message) { }

        public void Warn(string message) { }

        public void Error(string message, Exception? exception = null) { }
    }
}
