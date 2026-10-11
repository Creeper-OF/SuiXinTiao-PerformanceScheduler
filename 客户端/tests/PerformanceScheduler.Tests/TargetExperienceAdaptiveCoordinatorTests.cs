using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TargetExperienceAdaptiveCoordinatorTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateAndApplyAsync_WhenRollbackNotSupported_ReturnsNull()
    {
        var powerPlanManager = new FakePowerPlanManager();
        var rollbackManager = new FakeRollbackManager();
        var coordinator = new TargetExperienceAdaptiveCoordinator(powerPlanManager, rollbackManager);

        var app = CreateApp(100, "game.exe");
        var profile = CreateProfile(controlMode: TargetExperienceControlMode.Automatic);
        var capabilities = new CapabilitySnapshot
        {
            SupportsRollback = false,
            SupportsPowerPlanSwitching = true
        };
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var decision = CreateDecision(TargetExperienceAdjustmentKind.IncreasePerformance);

        var result = await coordinator.EvaluateAndApplyAsync(
            app,
            profile,
            capabilities,
            PowerSourceMode.Ac,
            activePlan,
            decision);

        Assert.Null(result);
        Assert.False(powerPlanManager.ApplyAdvancedSettingsCalled);
    }

    [Fact]
    public async Task EvaluateAndApplyAsync_WhenControlModeNotAutomatic_ReturnsNull()
    {
        var powerPlanManager = new FakePowerPlanManager();
        var rollbackManager = new FakeRollbackManager();
        var coordinator = new TargetExperienceAdaptiveCoordinator(powerPlanManager, rollbackManager);

        var app = CreateApp(100, "game.exe");
        var profile = CreateProfile(controlMode: TargetExperienceControlMode.RecommendOnly);
        var capabilities = new CapabilitySnapshot
        {
            SupportsRollback = true,
            SupportsPowerPlanSwitching = true
        };
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var decision = CreateDecision(TargetExperienceAdjustmentKind.IncreasePerformance);

        var result = await coordinator.EvaluateAndApplyAsync(
            app,
            profile,
            capabilities,
            PowerSourceMode.Ac,
            activePlan,
            decision);

        Assert.Null(result);
        Assert.False(powerPlanManager.ApplyAdvancedSettingsCalled);
    }

    [Fact]
    public async Task EvaluateAndApplyAsync_WhenStableAndActionable_AppliesSettingsAndReturnsResult()
    {
        var powerPlanManager = new FakePowerPlanManager { InitialProcessorMaxPercent = 80 };
        var rollbackManager = new FakeRollbackManager();
        var currentTime = InitialTime;
        var coordinator = new TargetExperienceAdaptiveCoordinator(
            powerPlanManager,
            rollbackManager,
            utcNow: () => currentTime);

        var app = CreateApp(100, "game.exe");
        var profile = CreateProfile(controlMode: TargetExperienceControlMode.Automatic);
        var capabilities = new CapabilitySnapshot
        {
            SupportsRollback = true,
            SupportsPowerPlanSwitching = true
        };
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var decision = CreateDecision(TargetExperienceAdjustmentKind.IncreasePerformance);

        var result = await coordinator.EvaluateAndApplyAsync(
            app,
            profile,
            capabilities,
            PowerSourceMode.Ac,
            activePlan,
            decision);

        Assert.NotNull(result);
        Assert.True(result.Applied);
        Assert.Equal(85, result.AppliedProcessorMaxPercent); // Stepped from 80 to 85
        Assert.Equal("TargetExperience.AdaptiveApplied", result.ReasonKey);
        Assert.True(powerPlanManager.ApplyAdvancedSettingsCalled);
        Assert.Equal(85, powerPlanManager.LastAppliedPreference?.ProcessorMaxStatePercent);
    }

    [Fact]
    public async Task EvaluateAndApplyAsync_WhenInCooldown_DoesNotApplyChanges()
    {
        var powerPlanManager = new FakePowerPlanManager { InitialProcessorMaxPercent = 80 };
        var rollbackManager = new FakeRollbackManager();
        var currentTime = InitialTime;
        var coordinator = new TargetExperienceAdaptiveCoordinator(
            powerPlanManager,
            rollbackManager,
            utcNow: () => currentTime);

        var app = CreateApp(100, "game.exe");
        var profile = CreateProfile(controlMode: TargetExperienceControlMode.Automatic);
        var capabilities = new CapabilitySnapshot
        {
            SupportsRollback = true,
            SupportsPowerPlanSwitching = true
        };
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var decision = CreateDecision(TargetExperienceAdjustmentKind.IncreasePerformance);

        // First apply
        await coordinator.EvaluateAndApplyAsync(app, profile, capabilities, PowerSourceMode.Ac, activePlan, decision);
        powerPlanManager.ApplyAdvancedSettingsCalled = false;

        // Second apply 2 seconds later (within 10s cooldown)
        currentTime = currentTime.AddSeconds(2);
        var secondResult = await coordinator.EvaluateAndApplyAsync(app, profile, capabilities, PowerSourceMode.Ac, activePlan, decision);

        Assert.NotNull(secondResult);
        Assert.False(secondResult.Applied);
        Assert.NotNull(secondResult.Plan);
        Assert.True(secondResult.Plan!.IsInCooldown);
        Assert.False(powerPlanManager.ApplyAdvancedSettingsCalled);
    }

    [Fact]
    public async Task ResetSessionAsync_RestoresBaselineAdvancedSettings()
    {
        var powerPlanManager = new FakePowerPlanManager { InitialProcessorMaxPercent = 80 };
        var rollbackManager = new FakeRollbackManager();
        var coordinator = new TargetExperienceAdaptiveCoordinator(
            powerPlanManager,
            rollbackManager,
            utcNow: () => InitialTime);

        var app = CreateApp(100, "game.exe");
        var profile = CreateProfile(controlMode: TargetExperienceControlMode.Automatic);
        var capabilities = new CapabilitySnapshot
        {
            SupportsRollback = true,
            SupportsPowerPlanSwitching = true
        };
        var activePlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var decision = CreateDecision(TargetExperienceAdjustmentKind.IncreasePerformance);

        // Apply changes: 80 -> 85
        await coordinator.EvaluateAndApplyAsync(app, profile, capabilities, PowerSourceMode.Ac, activePlan, decision);
        Assert.Equal(85, powerPlanManager.LastAppliedPreference?.ProcessorMaxStatePercent);

        // Reset session
        await coordinator.ResetSessionAsync();

        // Baseline (80%) should be restored
        Assert.Equal(80, powerPlanManager.LastAppliedPreference?.ProcessorMaxStatePercent);
    }

    private static FocusedAppContext CreateApp(int pid, string name) =>
        new()
        {
            ProcessId = pid,
            ProcessName = name,
            WindowTitle = name,
            WindowHandle = (nint)1
        };

    private static PerformanceProfile CreateProfile(TargetExperienceControlMode controlMode) =>
        new()
        {
            Name = "Adaptive Test Profile",
            TargetExperience = new TargetExperiencePreference
            {
                Enabled = true,
                ControlMode = controlMode
            }
        };

    private static TargetExperienceControlDecision CreateDecision(TargetExperienceAdjustmentKind kind) =>
        new()
        {
            State = kind == TargetExperienceAdjustmentKind.IncreasePerformance
                ? TargetExperienceControlState.NeedsMorePerformance
                : TargetExperienceControlState.NeedsCoolingOrPower,
            IsStable = true,
            EvidenceSampleCount = 5,
            StableSampleCount = 3,
            Recommendation = new TargetExperienceAdjustmentRecommendation
            {
                Kind = kind,
                StableSampleCount = 3,
                CanApplyAutomatically = true,
                ReasonKey = "TargetExperience.AutomaticReady"
            }
        };

    private sealed class FakePowerPlanManager : IPowerPlanManager
    {
        public int InitialProcessorMaxPercent { get; set; } = 100;
        public bool ApplyAdvancedSettingsCalled { get; set; }
        public PowerPlanAdvancedPreference? LastAppliedPreference { get; private set; }

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>([]);

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanInfo?>(null);

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(Guid schemeGuid, PowerSourceMode powerSourceMode, CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanAdvancedState?>(new PowerPlanAdvancedState
            {
                SchemeGuid = schemeGuid,
                PowerSourceMode = powerSourceMode,
                ProcessorMaxStatePercent = InitialProcessorMaxPercent
            });

        public Task<bool> ApplyAdvancedSettingsAsync(Guid schemeGuid, PowerSourceMode powerSourceMode, PowerPlanAdvancedPreference preference, CancellationToken cancellationToken = default)
        {
            ApplyAdvancedSettingsCalled = true;
            LastAppliedPreference = preference;
            return Task.FromResult(true);
        }

        public Task<bool> RestoreAdvancedSettingsAsync(PowerPlanAdvancedState state, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeRollbackManager : IRollbackManager
    {
        public Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SchedulerState?>(null);

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
