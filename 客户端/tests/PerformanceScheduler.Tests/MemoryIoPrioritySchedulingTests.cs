using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Infrastructure.Processes;
using PerformanceScheduler.Infrastructure.Rollback;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class MemoryIoPrioritySchedulingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _statePath;

    public MemoryIoPrioritySchedulingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "perf-scheduler-memio-tests-" + Guid.NewGuid());
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
    public void ProfileSafetyValidator_ValidatesMemoryIoPriorities()
    {
        var validProfile = new PerformanceProfile
        {
            Id = "valid-memio",
            Name = "Valid Memory and I/O Priorities",
            MemoryIoPriority = new MemoryIoPriorityPreference
            {
                Enabled = true,
                ForegroundIoPriority = IoPriorityLevel.High,
                BackgroundIoPriority = IoPriorityLevel.Low,
                ForegroundMemoryPriority = MemoryPriorityLevel.Normal,
                BackgroundMemoryPriority = MemoryPriorityLevel.Low
            },
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "worker",
                    TargetIoPriority = IoPriorityLevel.VeryLow,
                    TargetMemoryPriority = MemoryPriorityLevel.VeryLow
                }
            }
        };

        var validResult = ProfileSafetyValidator.Evaluate(validProfile);
        Assert.DoesNotContain(validResult.Issues, i => i.Code.Contains("IoPriority", StringComparison.OrdinalIgnoreCase) ||
                                                       i.Code.Contains("MemoryPriority", StringComparison.OrdinalIgnoreCase));

        var invalidProfile = new PerformanceProfile
        {
            Id = "invalid-memio",
            Name = "Invalid Memory and I/O Priorities",
            MemoryIoPriority = new MemoryIoPriorityPreference
            {
                Enabled = true,
                ForegroundIoPriority = (IoPriorityLevel)999,
                BackgroundIoPriority = IoPriorityLevel.High, // High is blocked for background
                ForegroundMemoryPriority = (MemoryPriorityLevel)999,
                BackgroundMemoryPriority = (MemoryPriorityLevel)999
            },
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "worker",
                    TargetIoPriority = IoPriorityLevel.Critical, // Critical is blocked for background
                    TargetMemoryPriority = (MemoryPriorityLevel)888
                }
            }
        };

        var invalidResult = ProfileSafetyValidator.Evaluate(invalidProfile);
        Assert.Contains(invalidResult.Issues, i => i.Code == "ForegroundIoPriorityInvalid");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundIoPriorityTooHigh");
        Assert.Contains(invalidResult.Issues, i => i.Code == "ForegroundMemoryPriorityInvalid");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundMemoryPriorityInvalid");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundPolicyIoPriorityTooHigh");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundPolicyMemoryPriorityInvalid");
    }

    [Fact]
    public async Task SchedulerActionExecutor_ExecutesMemoryIoActions_AndCapturesRollback()
    {
        var fakeManager = new FakeProcessMemoryIoManager();
        fakeManager.SetCurrentIoPriority(100, IoPriorityLevel.Normal);
        fakeManager.SetCurrentMemoryPriority(100, MemoryPriorityLevel.BelowNormal);

        var priorityManager = new FakePriorityManager();
        var powerPlanManager = new FakePowerPlanManager();
        var rollbackManager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger(),
            processMemoryIoManager: fakeManager);
        var executor = new SchedulerActionExecutor(
            powerPlanManager,
            priorityManager,
            rollbackManager,
            new TestLogger(),
            processMemoryIoManager: fakeManager);

        var profile = new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            MemoryIoPriority = new MemoryIoPriorityPreference
            {
                Enabled = true,
                ForegroundIoPriority = IoPriorityLevel.High,
                ForegroundMemoryPriority = MemoryPriorityLevel.Normal
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
            Capabilities = new CapabilitySnapshot { SupportsRollback = true, SupportsMemoryIoPriority = true },
            PowerSourceMode = PowerSourceMode.Ac
        };

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(IoPriorityLevel.High, fakeManager.GetCurrentIoPriority(100));
        Assert.Equal(MemoryPriorityLevel.Normal, fakeManager.GetCurrentMemoryPriority(100));
        Assert.Contains(result.Actions, r => r.Plan.Kind == SchedulerActionKind.ForegroundIoPriority && r.Status == SchedulerActionStatus.Applied);
        Assert.Contains(result.Actions, r => r.Plan.Kind == SchedulerActionKind.ForegroundMemoryPriority && r.Status == SchedulerActionStatus.Applied);

        // Verify state captured
        var capturedState = await rollbackManager.GetLastKnownStateAsync();
        Assert.NotNull(capturedState);
        Assert.True(capturedState.OriginalIoPriorities.ContainsKey(100));
        Assert.Equal(IoPriorityLevel.Normal, capturedState.OriginalIoPriorities[100]);
        Assert.Equal(IoPriorityLevel.High, capturedState.AppliedIoPriorities[100]);

        Assert.True(capturedState.OriginalMemoryPriorities.ContainsKey(100));
        Assert.Equal(MemoryPriorityLevel.BelowNormal, capturedState.OriginalMemoryPriorities[100]);
        Assert.Equal(MemoryPriorityLevel.Normal, capturedState.AppliedMemoryPriorities[100]);
    }

    [Fact]
    public async Task FileRollbackManager_RestoresMemoryIoPriorities_WhenUnchanged()
    {
        var fakeManager = new FakeProcessMemoryIoManager();
        fakeManager.SetCurrentIoPriority(100, IoPriorityLevel.High);
        fakeManager.SetCurrentMemoryPriority(100, MemoryPriorityLevel.Normal);

        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager();
        var rollbackManager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger(),
            processMemoryIoManager: fakeManager);

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
            OriginalIoPriorities = new Dictionary<int, IoPriorityLevel> { [100] = IoPriorityLevel.Normal },
            AppliedIoPriorities = new Dictionary<int, IoPriorityLevel> { [100] = IoPriorityLevel.High },
            OriginalMemoryPriorities = new Dictionary<int, MemoryPriorityLevel> { [100] = MemoryPriorityLevel.BelowNormal },
            AppliedMemoryPriorities = new Dictionary<int, MemoryPriorityLevel> { [100] = MemoryPriorityLevel.Normal }
        });

        await rollbackManager.RollbackAsync();

        Assert.Equal(IoPriorityLevel.Normal, fakeManager.GetCurrentIoPriority(100));
        Assert.Equal(MemoryPriorityLevel.BelowNormal, fakeManager.GetCurrentMemoryPriority(100));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsMemoryIoPriorities_WhenProcessExitedOrIdentityChanged()
    {
        var fakeManager = new FakeProcessMemoryIoManager { ShouldFailIdentity = true };
        fakeManager.SetCurrentIoPriority(100, IoPriorityLevel.High);
        fakeManager.SetCurrentMemoryPriority(100, MemoryPriorityLevel.Normal);

        var rollbackManager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(),
            new TestLogger(),
            processMemoryIoManager: fakeManager);

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
            OriginalIoPriorities = new Dictionary<int, IoPriorityLevel> { [100] = IoPriorityLevel.Normal },
            AppliedIoPriorities = new Dictionary<int, IoPriorityLevel> { [100] = IoPriorityLevel.High },
            OriginalMemoryPriorities = new Dictionary<int, MemoryPriorityLevel> { [100] = MemoryPriorityLevel.BelowNormal },
            AppliedMemoryPriorities = new Dictionary<int, MemoryPriorityLevel> { [100] = MemoryPriorityLevel.Normal }
        });

        await rollbackManager.RollbackAsync();

        // Should not revert because process identity mismatched / process exited
        Assert.Equal(IoPriorityLevel.High, fakeManager.GetCurrentIoPriority(100));
        Assert.Equal(MemoryPriorityLevel.Normal, fakeManager.GetCurrentMemoryPriority(100));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsMemoryIoPriorities_WhenUserManuallyChangedPriorities()
    {
        var fakeManager = new FakeProcessMemoryIoManager();
        fakeManager.SetCurrentIoPriority(100, IoPriorityLevel.VeryLow); // User manually changed to VeryLow
        fakeManager.SetCurrentMemoryPriority(100, MemoryPriorityLevel.Low); // User manually changed to Low

        var rollbackManager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(),
            new TestLogger(),
            processMemoryIoManager: fakeManager);

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
            OriginalIoPriorities = new Dictionary<int, IoPriorityLevel> { [100] = IoPriorityLevel.Normal },
            AppliedIoPriorities = new Dictionary<int, IoPriorityLevel> { [100] = IoPriorityLevel.High },
            OriginalMemoryPriorities = new Dictionary<int, MemoryPriorityLevel> { [100] = MemoryPriorityLevel.BelowNormal },
            AppliedMemoryPriorities = new Dictionary<int, MemoryPriorityLevel> { [100] = MemoryPriorityLevel.Normal }
        });

        await rollbackManager.RollbackAsync();

        // Values should remain untouched at user's manual changes
        Assert.Equal(IoPriorityLevel.VeryLow, fakeManager.GetCurrentIoPriority(100));
        Assert.Equal(MemoryPriorityLevel.Low, fakeManager.GetCurrentMemoryPriority(100));
        Assert.False(File.Exists(_statePath));
    }

    private sealed class FakeProcessMemoryIoManager : IProcessMemoryIoManager
    {
        private readonly Dictionary<int, IoPriorityLevel> _ioPriorities = new();
        private readonly Dictionary<int, MemoryPriorityLevel> _memoryPriorities = new();
        public bool ShouldFailIdentity { get; set; }

        public void SetCurrentIoPriority(int pid, IoPriorityLevel level) => _ioPriorities[pid] = level;
        public IoPriorityLevel GetCurrentIoPriority(int pid) => _ioPriorities.GetValueOrDefault(pid);

        public void SetCurrentMemoryPriority(int pid, MemoryPriorityLevel level) => _memoryPriorities[pid] = level;
        public MemoryPriorityLevel GetCurrentMemoryPriority(int pid) => _memoryPriorities.GetValueOrDefault(pid);

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

        public Task<IoPriorityLevel?> TryGetIoPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IoPriorityLevel?>(_ioPriorities.TryGetValue(processId, out var level) ? level : null);

        public Task<bool> SetIoPriorityAsync(int processId, IoPriorityLevel priority, CancellationToken cancellationToken = default)
        {
            _ioPriorities[processId] = priority;
            return Task.FromResult(true);
        }

        public Task<MemoryPriorityLevel?> TryGetMemoryPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryPriorityLevel?>(_memoryPriorities.TryGetValue(processId, out var level) ? level : null);

        public Task<bool> SetMemoryPriorityAsync(int processId, MemoryPriorityLevel priority, CancellationToken cancellationToken = default)
        {
            _memoryPriorities[processId] = priority;
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
        public void Trace(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
