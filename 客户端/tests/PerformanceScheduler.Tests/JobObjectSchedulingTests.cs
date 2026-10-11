using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Infrastructure.Rollback;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class JobObjectSchedulingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _statePath;

    public JobObjectSchedulingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "perf-scheduler-jobobj-tests-" + Guid.NewGuid());
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
    public void ProfileSafetyValidator_ValidatesJobObjectLimits()
    {
        var validProfile = new PerformanceProfile
        {
            Id = "valid-jobobj",
            Name = "Valid Job Object Limits",
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "worker1",
                    JobObjectLimits = new JobObjectLimitPreference
                    {
                        CpuRate = new JobObjectCpuRatePreference
                        {
                            Enabled = true,
                            MaxCpuRatePercent = 25.0,
                            HardCap = true
                        },
                        MemoryLimit = new JobObjectMemoryLimitPreference
                        {
                            Enabled = true,
                            MaxCommitMemoryBytes = 512UL * 1024 * 1024
                        }
                    }
                },
                new BackgroundProcessPolicy
                {
                    Category = "worker2",
                    TargetJobCpuRatePercent = 50.0,
                    TargetJobMemoryLimitBytes = 1024UL * 1024 * 1024
                }
            }
        };

        var validResult = ProfileSafetyValidator.Evaluate(validProfile);
        Assert.DoesNotContain(validResult.Issues, i => i.Code.Contains("JobCpuRate", StringComparison.OrdinalIgnoreCase) ||
                                                       i.Code.Contains("JobMemoryLimit", StringComparison.OrdinalIgnoreCase));

        var invalidProfile = new PerformanceProfile
        {
            Id = "invalid-jobobj",
            Name = "Invalid Job Object Limits",
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "bad-rate",
                    JobObjectLimits = new JobObjectLimitPreference
                    {
                        CpuRate = new JobObjectCpuRatePreference
                        {
                            Enabled = true,
                            MaxCpuRatePercent = 0.5 // Out of range (< 1.0)
                        }
                    }
                },
                new BackgroundProcessPolicy
                {
                    Category = "rate-too-high",
                    TargetJobCpuRatePercent = 105.0 // Out of range (> 100.0)
                },
                new BackgroundProcessPolicy
                {
                    Category = "zero-memory",
                    JobObjectLimits = new JobObjectLimitPreference
                    {
                        MemoryLimit = new JobObjectMemoryLimitPreference
                        {
                            Enabled = true,
                            MaxCommitMemoryBytes = 0 // Invalid (must be > 0)
                        }
                    }
                },
                new BackgroundProcessPolicy
                {
                    Category = "zero-memory-prop",
                    TargetJobMemoryLimitBytes = 0 // Invalid (must be > 0)
                }
            }
        };

        var invalidResult = ProfileSafetyValidator.Evaluate(invalidProfile);
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundPolicyJobCpuRateInvalid");
        Assert.Contains(invalidResult.Issues, i => i.Code == "BackgroundPolicyJobMemoryLimitInvalid");
    }

    [Fact]
    public async Task SchedulerActionExecutor_ExecutesJobObjectActions_AndCapturesRollback()
    {
        var fakeManager = new FakeProcessJobObjectManager();
        var fakePriority = new FakePriorityManager();
        var powerPlanManager = new FakePowerPlanManager();
        var rollbackManager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            fakePriority,
            new TestLogger(),
            processJobObjectManager: fakeManager);
        var executor = new SchedulerActionExecutor(
            powerPlanManager,
            fakePriority,
            rollbackManager,
            new TestLogger(),
            processJobObjectManager: fakeManager);

        var bgIdentity = new ProcessIdentitySnapshot
        {
            ProcessId = 200,
            ProcessName = "bg_worker.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        var targetLimits = new JobObjectLimitPreference
        {
            CpuRate = new JobObjectCpuRatePreference
            {
                Enabled = true,
                MaxCpuRatePercent = 20.0,
                HardCap = true
            },
            MemoryLimit = new JobObjectMemoryLimitPreference
            {
                Enabled = true,
                MaxCommitMemoryBytes = 256UL * 1024 * 1024
            }
        };

        fakePriority.AddBaseline(new BackgroundProcessPriorityBaseline
        {
            ProcessId = 200,
            Identity = bgIdentity,
            ProcessName = "bg_worker.exe",
            Category = "worker",
            PreviousPriority = PriorityLevel.Normal,
            TargetPriority = PriorityLevel.BelowNormal,
            TargetJobLimits = targetLimits
        });

        var profile = new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "worker",
                    JobObjectLimits = targetLimits
                }
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
            Capabilities = new CapabilitySnapshot { SupportsRollback = true, SupportsJobObjectLimits = true },
            PowerSourceMode = PowerSourceMode.Ac
        };

        var result = await executor.ExecuteAsync(request);

        var currentLimits = await fakeManager.GetCurrentLimitsAsync(200);
        Assert.NotNull(currentLimits);
        Assert.True(currentLimits.HasCpuRateControl);
        Assert.Equal(20.0, currentLimits.CpuRatePercent);
        Assert.True(currentLimits.HasMemoryLimit);
        Assert.Equal(256UL * 1024 * 1024, currentLimits.MaxCommitMemoryBytes);

        Assert.Contains(result.Actions, r => r.Plan.Kind == SchedulerActionKind.BackgroundJobObjectLimit &&
                                             r.Status == SchedulerActionStatus.Applied);

        var capturedState = await rollbackManager.GetLastKnownStateAsync();
        Assert.NotNull(capturedState);
        Assert.True(capturedState.AppliedJobObjectLimits.ContainsKey(200));
        var capturedLimits = capturedState.AppliedJobObjectLimits[200];
        Assert.True(capturedLimits.HasCpuRateControl);
        Assert.Equal(20.0, capturedLimits.CpuRatePercent);
        Assert.True(capturedLimits.HasMemoryLimit);
        Assert.Equal(256UL * 1024 * 1024, capturedLimits.MaxCommitMemoryBytes);
    }

    [Fact]
    public async Task FileRollbackManager_RemovesJobObjectLimits_WhenUnchanged()
    {
        var fakeManager = new FakeProcessJobObjectManager();
        fakeManager.SetCurrentLimits(200, new JobObjectLimitSnapshot
        {
            ProcessId = 200,
            HasCpuRateControl = true,
            CpuRatePercent = 20.0,
            CpuRateHardCap = true,
            HasMemoryLimit = true,
            MaxCommitMemoryBytes = 256UL * 1024 * 1024
        });

        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager();
        var rollbackManager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger(),
            processJobObjectManager: fakeManager);

        var identity = new ProcessIdentitySnapshot
        {
            ProcessId = 200,
            ProcessName = "bg_worker.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        await rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [200] = identity },
            AppliedJobObjectLimits = new Dictionary<int, JobObjectLimitSnapshot>
            {
                [200] = new JobObjectLimitSnapshot
                {
                    ProcessId = 200,
                    HasCpuRateControl = true,
                    CpuRatePercent = 20.0,
                    CpuRateHardCap = true,
                    HasMemoryLimit = true,
                    MaxCommitMemoryBytes = 256UL * 1024 * 1024
                }
            }
        });

        await rollbackManager.RollbackAsync();

        var limits = await fakeManager.GetCurrentLimitsAsync(200);
        Assert.NotNull(limits);
        Assert.False(limits.HasCpuRateControl);
        Assert.False(limits.HasMemoryLimit);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsJobObjectLimits_WhenProcessExitedOrIdentityChanged()
    {
        var fakeManager = new FakeProcessJobObjectManager { ShouldFailIdentity = true };
        fakeManager.SetCurrentLimits(200, new JobObjectLimitSnapshot
        {
            ProcessId = 200,
            HasCpuRateControl = true,
            CpuRatePercent = 20.0,
            CpuRateHardCap = true,
            HasMemoryLimit = true,
            MaxCommitMemoryBytes = 256UL * 1024 * 1024
        });

        var rollbackManager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(),
            new TestLogger(),
            processJobObjectManager: fakeManager);

        var identity = new ProcessIdentitySnapshot
        {
            ProcessId = 200,
            ProcessName = "bg_worker.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        await rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [200] = identity },
            AppliedJobObjectLimits = new Dictionary<int, JobObjectLimitSnapshot>
            {
                [200] = new JobObjectLimitSnapshot
                {
                    ProcessId = 200,
                    HasCpuRateControl = true,
                    CpuRatePercent = 20.0,
                    CpuRateHardCap = true,
                    HasMemoryLimit = true,
                    MaxCommitMemoryBytes = 256UL * 1024 * 1024
                }
            }
        });

        await rollbackManager.RollbackAsync();

        // Process identity mismatch: limits remain intact and are not removed
        var limits = await fakeManager.GetCurrentLimitsAsync(200);
        Assert.NotNull(limits);
        Assert.True(limits.HasCpuRateControl);
        Assert.True(limits.HasMemoryLimit);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task FileRollbackManager_SkipsJobObjectLimits_WhenTamperedOrCurrentValueMismatch()
    {
        var fakeManager = new FakeProcessJobObjectManager();
        // Externally tampered to 50% CPU and 512MB
        fakeManager.SetCurrentLimits(200, new JobObjectLimitSnapshot
        {
            ProcessId = 200,
            HasCpuRateControl = true,
            CpuRatePercent = 50.0,
            CpuRateHardCap = true,
            HasMemoryLimit = true,
            MaxCommitMemoryBytes = 512UL * 1024 * 1024
        });

        var rollbackManager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(),
            new TestLogger(),
            processJobObjectManager: fakeManager);

        var identity = new ProcessIdentitySnapshot
        {
            ProcessId = 200,
            ProcessName = "bg_worker.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        await rollbackManager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [200] = identity },
            AppliedJobObjectLimits = new Dictionary<int, JobObjectLimitSnapshot>
            {
                [200] = new JobObjectLimitSnapshot
                {
                    ProcessId = 200,
                    HasCpuRateControl = true,
                    CpuRatePercent = 20.0,
                    CpuRateHardCap = true,
                    HasMemoryLimit = true,
                    MaxCommitMemoryBytes = 256UL * 1024 * 1024
                }
            }
        });

        await rollbackManager.RollbackAsync();

        // Should skip rollback because current limits do not match expected applied limits
        var limits = await fakeManager.GetCurrentLimitsAsync(200);
        Assert.NotNull(limits);
        Assert.Equal(50.0, limits.CpuRatePercent);
        Assert.Equal(512UL * 1024 * 1024, limits.MaxCommitMemoryBytes);
        Assert.False(File.Exists(_statePath));
    }

    private sealed class FakeProcessJobObjectManager : IProcessJobObjectManager
    {
        private readonly Dictionary<int, JobObjectLimitSnapshot> _limits = new();
        public bool ShouldFailIdentity { get; set; }

        public void SetCurrentLimits(int processId, JobObjectLimitSnapshot snapshot)
        {
            _limits[processId] = snapshot;
        }

        public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(int processId, CancellationToken cancellationToken = default)
        {
            if (ShouldFailIdentity)
            {
                return Task.FromResult<ProcessIdentitySnapshot?>(null);
            }

            return Task.FromResult<ProcessIdentitySnapshot?>(new ProcessIdentitySnapshot
            {
                ProcessId = processId,
                ProcessName = "bg_worker.exe",
                StartedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<bool> IsSameProcessAsync(ProcessIdentitySnapshot identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(!ShouldFailIdentity);

        public Task<bool> ApplyProcessLimitsAsync(int processId, JobObjectLimitPreference limits, CancellationToken cancellationToken = default)
        {
            _limits[processId] = new JobObjectLimitSnapshot
            {
                ProcessId = processId,
                HasCpuRateControl = limits.CpuRate.Enabled,
                CpuRatePercent = limits.CpuRate.Enabled ? limits.CpuRate.MaxCpuRatePercent : null,
                CpuRateHardCap = limits.CpuRate.Enabled && limits.CpuRate.HardCap,
                HasMemoryLimit = limits.MemoryLimit.Enabled,
                MaxCommitMemoryBytes = limits.MemoryLimit.Enabled ? limits.MemoryLimit.MaxCommitMemoryBytes : null
            };
            return Task.FromResult(true);
        }

        public Task<bool> RemoveProcessLimitsAsync(int processId, CancellationToken cancellationToken = default)
        {
            _limits[processId] = new JobObjectLimitSnapshot
            {
                ProcessId = processId,
                HasCpuRateControl = false,
                CpuRatePercent = null,
                HasMemoryLimit = false,
                MaxCommitMemoryBytes = null
            };
            return Task.FromResult(true);
        }

        public Task<JobObjectLimitSnapshot?> GetCurrentLimitsAsync(int processId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<JobObjectLimitSnapshot?>(_limits.TryGetValue(processId, out var limits)
                ? limits
                : new JobObjectLimitSnapshot { ProcessId = processId });
        }
    }

    private sealed class FakePriorityManager : IPriorityManager
    {
        private readonly List<BackgroundProcessPriorityBaseline> _baselines = new();

        public void AddBaseline(BackgroundProcessPriorityBaseline baseline) => _baselines.Add(baseline);

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
            Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(_baselines);

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
