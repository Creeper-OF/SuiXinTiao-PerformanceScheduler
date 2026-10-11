using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Infrastructure.Processes;
using PerformanceScheduler.Infrastructure.Rollback;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class CpuSetSchedulingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _statePath;

    public CpuSetSchedulingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "perf-scheduler-cpuset-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDirectory);
        _statePath = Path.Combine(_testDirectory, "scheduler-state.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void ProfileSafetyValidator_ValidatesCpuSets()
    {
        var validProfile = new PerformanceProfile
        {
            Id = "valid-cpusets",
            Name = "Valid CPU Sets",
            CpuSets = new CpuSetPreference
            {
                Enabled = true,
                ForegroundCpuSets = new uint[] { 0, 1 },
                BackgroundCpuSets = new uint[] { 2, 3 }
            }
        };

        var validResult = ProfileSafetyValidator.Evaluate(validProfile);
        Assert.DoesNotContain(validResult.Issues, i => i.Code.Contains("CpuSet", StringComparison.OrdinalIgnoreCase));

        var invalidProfile = new PerformanceProfile
        {
            Id = "invalid-cpusets",
            Name = "Invalid CPU Sets",
            CpuSets = new CpuSetPreference
            {
                Enabled = true,
                ForegroundCpuSets = Array.Empty<uint>(),
                BackgroundCpuSets = Array.Empty<uint>()
            },
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "worker",
                    TargetCpuSets = Array.Empty<uint>()
                }
            }
        };

        var invalidResult = ProfileSafetyValidator.Evaluate(invalidProfile);
        Assert.Contains(invalidResult.Issues, i => i.Code == "ForegroundCpuSetsEmpty");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundCpuSetsEmpty");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundPolicyCpuSetsEmpty");
    }

    [Fact]
    public async Task SchedulerActionExecutor_ExecutesCpuSetActions_AndCapturesRollback()
    {
        var fakeCpuSetManager = new FakeCpuSetManager();
        fakeCpuSetManager.SetCurrentCpuSets(100, new uint[] { 0, 1, 2, 3 }); // FG process
        fakeCpuSetManager.SetCurrentCpuSets(200, new uint[] { 0, 1, 2, 3 }); // BG process

        var priorityManager = new FakePriorityManager();
        var powerPlanManager = new FakePowerPlanManager();
        var rollbackManager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger(),
            cpuSetManager: fakeCpuSetManager);
        var executor = new SchedulerActionExecutor(
            powerPlanManager,
            priorityManager,
            rollbackManager,
            new TestLogger(),
            cpuSetManager: fakeCpuSetManager);

        var profile = new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            CpuSets = new CpuSetPreference
            {
                Enabled = true,
                ForegroundCpuSets = new uint[] { 0, 1 },
                BackgroundCpuSets = new uint[] { 2, 3 }
            }
        };

        var request = new SchedulerActionExecutionRequest
        {
            TransactionId = Guid.NewGuid(),
            ActiveApp = new FocusedAppContext
            {
                ProcessId = 100,
                ProcessName = "game.exe",
                WindowTitle = "Game",
                WindowHandle = (nint)1234
            },
            Match = new ProfileMatchResult(profile, 100, "test"),
            Capabilities = new CapabilitySnapshot { SupportsRollback = true, SupportsCpuSets = true },
            PowerSourceMode = PowerSourceMode.Ac
        };

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(new uint[] { 0, 1 }, fakeCpuSetManager.GetCurrentCpuSets(100));
        Assert.Contains(result.Actions, r => r.Plan.Kind == SchedulerActionKind.ForegroundCpuSet && r.Status == SchedulerActionStatus.Applied);

        // Verify state captured
        var capturedState = await rollbackManager.GetLastKnownStateAsync();
        Assert.NotNull(capturedState);
        Assert.True(capturedState.OriginalCpuSets.ContainsKey(100));
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, capturedState.OriginalCpuSets[100]);
        Assert.Equal(new uint[] { 0, 1 }, capturedState.AppliedCpuSets[100]);
    }

    [Fact]
    public async Task FileRollbackManager_RestoresCpuSets_WhenUnchanged()
    {
        var fakeCpuSetManager = new FakeCpuSetManager();
        fakeCpuSetManager.SetCurrentCpuSets(100, new uint[] { 0, 1 }); // Already applied {0, 1}, original was {0, 1, 2, 3}

        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager();
        var rollbackManager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger(),
            cpuSetManager: fakeCpuSetManager);

        var identity = new ProcessIdentitySnapshot
        {
            ProcessId = 100,
            ProcessName = "game.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        await rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [100] = identity },
            OriginalCpuSets = new Dictionary<int, IReadOnlyList<uint>> { [100] = new uint[] { 0, 1, 2, 3 } },
            AppliedCpuSets = new Dictionary<int, IReadOnlyList<uint>> { [100] = new uint[] { 0, 1 } }
        });

        await rollbackManager.RollbackAsync();

        Assert.Equal(new uint[] { 0, 1, 2, 3 }, fakeCpuSetManager.GetCurrentCpuSets(100));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsCpuSets_WhenProcessExitedOrIdentityChanged()
    {
        var fakeCpuSetManager = new FakeCpuSetManager { ShouldFailIdentity = true };
        fakeCpuSetManager.SetCurrentCpuSets(100, new uint[] { 0, 1 });

        var rollbackManager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(),
            new TestLogger(),
            cpuSetManager: fakeCpuSetManager);

        var identity = new ProcessIdentitySnapshot
        {
            ProcessId = 100,
            ProcessName = "game.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        await rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [100] = identity },
            OriginalCpuSets = new Dictionary<int, IReadOnlyList<uint>> { [100] = new uint[] { 0, 1, 2, 3 } },
            AppliedCpuSets = new Dictionary<int, IReadOnlyList<uint>> { [100] = new uint[] { 0, 1 } }
        });

        await rollbackManager.RollbackAsync();

        // Should not have reverted CPU sets because process identity mismatched (e.g. process exited)
        Assert.Equal(new uint[] { 0, 1 }, fakeCpuSetManager.GetCurrentCpuSets(100));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsCpuSets_WhenUserManuallyChangedCpuSets()
    {
        var fakeCpuSetManager = new FakeCpuSetManager();
        fakeCpuSetManager.SetCurrentCpuSets(100, new uint[] { 0, 2 }); // Changed from applied {0, 1} to {0, 2}

        var rollbackManager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(),
            new TestLogger(),
            cpuSetManager: fakeCpuSetManager);

        var identity = new ProcessIdentitySnapshot
        {
            ProcessId = 100,
            ProcessName = "game.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        await rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [100] = identity },
            OriginalCpuSets = new Dictionary<int, IReadOnlyList<uint>> { [100] = new uint[] { 0, 1, 2, 3 } },
            AppliedCpuSets = new Dictionary<int, IReadOnlyList<uint>> { [100] = new uint[] { 0, 1 } }
        });

        await rollbackManager.RollbackAsync();

        // CPU sets should remain untouched at {0, 2}
        Assert.Equal(new uint[] { 0, 2 }, fakeCpuSetManager.GetCurrentCpuSets(100));
        Assert.False(File.Exists(_statePath));
    }

    private sealed class FakeCpuSetManager : ICpuSetManager
    {
        private readonly Dictionary<int, IReadOnlyList<uint>> _cpuSets = new();
        public bool ShouldFailIdentity { get; set; }

        public void SetCurrentCpuSets(int pid, IReadOnlyList<uint> sets) => _cpuSets[pid] = sets;
        public IReadOnlyList<uint>? GetCurrentCpuSets(int pid) => _cpuSets.GetValueOrDefault(pid);

        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(int processId, CancellationToken cancellationToken = default)
        {
            if (ShouldFailIdentity)
            {
                return Task.FromResult<ProcessIdentitySnapshot?>(null);
            }

            return Task.FromResult<ProcessIdentitySnapshot?>(new ProcessIdentitySnapshot
            {
                ProcessId = processId,
                ProcessName = "game.exe",
                StartedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<bool> IsSameProcessAsync(ProcessIdentitySnapshot identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(!ShouldFailIdentity);

        public Task<IReadOnlyList<uint>?> TryGetCpuSetsAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<uint>?>(_cpuSets.TryGetValue(processId, out var sets) ? sets : null);

        public Task<bool> SetCpuSetsAsync(int processId, IReadOnlyList<uint>? cpuSetIds, CancellationToken cancellationToken = default)
        {
            if (cpuSetIds is null)
            {
                _cpuSets.Remove(processId);
            }
            else
            {
                _cpuSets[processId] = cpuSetIds;
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<uint>> GetSystemCpuSetIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<uint>>(new uint[] { 0, 1, 2, 3 });
    }

    private sealed class FakePriorityManager : IPriorityManager
    {
        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessIdentitySnapshot?>(new ProcessIdentitySnapshot { ProcessId = processId, ProcessName = "proc" });

        public Task<bool> IsSameProcessAsync(ProcessIdentitySnapshot identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(PriorityLevel.Normal);

        public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ConditionalProcessUpdateResult> RestorePriorityIfUnchangedAsync(ProcessIdentitySnapshot identity, PriorityLevel expectedCurrentPriority, PriorityLevel originalPriority, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConditionalProcessUpdateResult.Applied);

        public Task<ConditionalProcessUpdateResult> RestoreEfficiencyModeIfUnchangedAsync(ProcessIdentitySnapshot identity, bool expectedCurrentEfficiencyMode, ProcessEfficiencyModeState originalEfficiencyMode, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConditionalProcessUpdateResult.Applied);

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(FocusedAppContext app, PerformanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult { ProcessId = app.ProcessId, Status = SchedulerActionStatus.Applied });

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(FocusedAppContext app, PerformanceProfile profile, ProcessIdentitySnapshot expectedIdentity, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult { ProcessId = app.ProcessId, Status = SchedulerActionStatus.Applied });

        public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(FocusedAppContext foregroundApp, PerformanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(Array.Empty<BackgroundProcessPriorityBaseline>());

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(FocusedAppContext foregroundApp, PerformanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(Array.Empty<BackgroundProcessAdjustmentResult>());

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(FocusedAppContext foregroundApp, PerformanceProfile profile, IReadOnlyList<BackgroundProcessPriorityBaseline> preparedBaselines, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(Array.Empty<BackgroundProcessAdjustmentResult>());
    }

    private sealed class FakePowerPlanManager : IPowerPlanManager
    {
        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>(Array.Empty<PowerPlanInfo>());

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanInfo?>(null);

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(Guid schemeGuid, PowerSourceMode powerSourceMode, CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanAdvancedState?>(null);

        public Task<bool> ApplyAdvancedSettingsAsync(Guid schemeGuid, PowerSourceMode powerSourceMode, PowerPlanAdvancedPreference preference, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RestoreAdvancedSettingsAsync(PowerPlanAdvancedState state, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestLogger : IAppLogger
    {
        public event EventHandler<LogEntry>? EntryWritten { add { } remove { } }
        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) => Array.Empty<LogEntry>();
        public void Trace(string message) {}
        public void Info(string message) {}
        public void Warn(string message) {}
        public void Error(string message, Exception? exception = null) {}
    }
}
