using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Infrastructure.Processes;
using PerformanceScheduler.Infrastructure.Rollback;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class CpuAffinitySchedulingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _statePath;

    public CpuAffinitySchedulingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "perf-scheduler-affinity-tests-" + Guid.NewGuid());
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
    public void ProfileSafetyValidator_ValidatesCpuAffinityMasks()
    {
        var validProfile = new PerformanceProfile
        {
            Id = "valid-affinity",
            Name = "Valid Affinity",
            CpuAffinity = new CpuAffinityPreference
            {
                Enabled = true,
                ForegroundAffinityMask = 0x3,
                BackgroundAffinityMask = 0x1
            }
        };

        var validResult = ProfileSafetyValidator.Evaluate(validProfile);
        Assert.DoesNotContain(validResult.Issues, i => i.Code.Contains("Affinity", StringComparison.OrdinalIgnoreCase));

        var invalidProfile = new PerformanceProfile
        {
            Id = "invalid-affinity",
            Name = "Invalid Affinity",
            CpuAffinity = new CpuAffinityPreference
            {
                Enabled = true,
                ForegroundAffinityMask = -1,
                BackgroundAffinityMask = 0
            },
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "worker",
                    TargetAffinityMask = -5
                }
            }
        };

        var invalidResult = ProfileSafetyValidator.Evaluate(invalidProfile);
        Assert.Contains(invalidResult.Issues, i => i.Code == "ForegroundAffinityMaskInvalid");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundAffinityMaskInvalid");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundPolicyAffinityMaskInvalid");
    }

    [Fact]
    public async Task SchedulerActionExecutor_ExecutesAffinityActions_AndCapturesRollback()
    {
        var fakeAffinityManager = new FakeAffinityManager();
        fakeAffinityManager.SetCurrentAffinity(100, 0xF); // FG process
        fakeAffinityManager.SetCurrentAffinity(200, 0xF); // BG process

        var priorityManager = new FakePriorityManager();
        var powerPlanManager = new FakePowerPlanManager();
        var rollbackManager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, new TestLogger(), fakeAffinityManager);
        var executor = new SchedulerActionExecutor(
            powerPlanManager,
            priorityManager,
            rollbackManager,
            new TestLogger(),
            cpuAffinityManager: fakeAffinityManager);

        var profile = new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            CpuAffinity = new CpuAffinityPreference
            {
                Enabled = true,
                ForegroundAffinityMask = 0x3, // Cores 0-1
                BackgroundAffinityMask = 0xC  // Cores 2-3
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
            Capabilities = new CapabilitySnapshot { SupportsRollback = true, SupportsCpuAffinity = true },
            PowerSourceMode = PowerSourceMode.Ac
        };

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(0x3, fakeAffinityManager.GetCurrentAffinity(100));
        Assert.Contains(result.Actions, r => r.Plan.Kind == SchedulerActionKind.ForegroundCpuAffinity && r.Status == SchedulerActionStatus.Applied);

        // Verify state captured
        var capturedState = await rollbackManager.GetLastKnownStateAsync();
        Assert.NotNull(capturedState);
        Assert.True(capturedState.OriginalCpuAffinities.ContainsKey(100));
        Assert.Equal(0xF, capturedState.OriginalCpuAffinities[100]);
        Assert.Equal(0x3, capturedState.AppliedCpuAffinities[100]);
    }

    [Fact]
    public async Task FileRollbackManager_RestoresCpuAffinity_WhenUnchanged()
    {
        var fakeAffinityManager = new FakeAffinityManager();
        fakeAffinityManager.SetCurrentAffinity(100, 0x3); // Already applied 0x3, original was 0xF

        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager();
        var rollbackManager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, new TestLogger(), fakeAffinityManager);

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
            OriginalCpuAffinities = new Dictionary<int, long> { [100] = 0xF },
            AppliedCpuAffinities = new Dictionary<int, long> { [100] = 0x3 }
        });

        await rollbackManager.RollbackAsync();

        Assert.Equal(0xF, fakeAffinityManager.GetCurrentAffinity(100));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsCpuAffinity_WhenProcessExitedOrIdentityChanged()
    {
        var fakeAffinityManager = new FakeAffinityManager { ShouldFailIdentity = true };
        fakeAffinityManager.SetCurrentAffinity(100, 0x3);

        var rollbackManager = new FileRollbackManager(_statePath, new FakePowerPlanManager(), new FakePriorityManager(), new TestLogger(), fakeAffinityManager);

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
            OriginalCpuAffinities = new Dictionary<int, long> { [100] = 0xF },
            AppliedCpuAffinities = new Dictionary<int, long> { [100] = 0x3 }
        });

        await rollbackManager.RollbackAsync();

        // Should not have reverted affinity because process identity mismatched (e.g. process exited)
        Assert.Equal(0x3, fakeAffinityManager.GetCurrentAffinity(100));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsCpuAffinity_WhenUserManuallyChangedAffinity()
    {
        var fakeAffinityManager = new FakeAffinityManager();
        fakeAffinityManager.SetCurrentAffinity(100, 0x7); // Changed from applied 0x3 to 0x7

        var rollbackManager = new FileRollbackManager(_statePath, new FakePowerPlanManager(), new FakePriorityManager(), new TestLogger(), fakeAffinityManager);

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
            OriginalCpuAffinities = new Dictionary<int, long> { [100] = 0xF },
            AppliedCpuAffinities = new Dictionary<int, long> { [100] = 0x3 }
        });

        await rollbackManager.RollbackAsync();

        // Affinity should remain untouched at 0x7
        Assert.Equal(0x7, fakeAffinityManager.GetCurrentAffinity(100));
        Assert.False(File.Exists(_statePath));
    }

    private sealed class FakeAffinityManager : ICpuAffinityManager
    {
        private readonly Dictionary<int, long> _affinities = new();
        public bool ShouldFailIdentity { get; set; }

        public void SetCurrentAffinity(int pid, long mask) => _affinities[pid] = mask;
        public long GetCurrentAffinity(int pid) => _affinities.GetValueOrDefault(pid);

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

        public Task<long?> TryGetAffinityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(_affinities.TryGetValue(processId, out var mask) ? mask : null);

        public Task<bool> SetAffinityAsync(int processId, long affinityMask, CancellationToken cancellationToken = default)
        {
            _affinities[processId] = affinityMask;
            return Task.FromResult(true);
        }
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