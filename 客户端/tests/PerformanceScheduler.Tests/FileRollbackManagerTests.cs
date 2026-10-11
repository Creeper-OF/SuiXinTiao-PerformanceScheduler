using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Rollback;

namespace PerformanceScheduler.Tests;

public sealed class FileRollbackManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _statePath;

    public FileRollbackManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceScheduler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _statePath = Path.Combine(_tempDirectory, "rollback-state.json");
    }

    [Fact]
    public async Task RollbackAsync_ContinuesWhenOneProcessPriorityCannotBeRestored()
    {
        var originalPowerPlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: false);
        var appliedPowerPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var powerPlanManager = new FakePowerPlanManager
        {
            CurrentPowerPlan = appliedPowerPlan
        };
        var priorityManager = new FakePriorityManager(
            failingProcessId: 10,
            currentPriorities: new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.BelowNormal,
                [20] = PriorityLevel.Idle
            });
        var logger = new TestLogger();
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, logger);

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousPowerPlan = originalPowerPlan,
            AppliedPowerPlan = appliedPowerPlan,
            OriginalPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.Normal,
                [20] = PriorityLevel.BelowNormal
            },
            AppliedPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.BelowNormal,
                [20] = PriorityLevel.Idle
            },
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot>
            {
                [10] = CreateIdentity(10),
                [20] = CreateIdentity(20)
            }
        });

        await manager.RollbackAsync();

        Assert.True(powerPlanManager.SetActivePlanCalled);
        Assert.Equal(PriorityLevel.BelowNormal, priorityManager.RestoredPriorities[20]);
        Assert.True(File.Exists(_statePath));
        Assert.Contains(logger.Messages, message => message.Contains("PID 10", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetLastKnownStateAsync_RejectsDirectoryAtRollbackStatePath()
    {
        Directory.CreateDirectory(_statePath);
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.GetLastKnownStateAsync());
    }

    [Fact]
    public async Task RollbackAsync_RestoresAdvancedPowerSettingsWhenCaptured()
    {
        var schemeGuid = Guid.NewGuid();
        var appliedState = new PowerPlanAdvancedState
        {
            SchemeGuid = schemeGuid,
            PowerSourceMode = PowerSourceMode.Battery,
            ProcessorMaxStatePercent = 80
        };
        var powerPlanManager = new FakePowerPlanManager
        {
            CurrentAdvancedState = appliedState
        };
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousAdvancedPowerSettings = new PowerPlanAdvancedState
            {
                SchemeGuid = schemeGuid,
                PowerSourceMode = PowerSourceMode.Battery,
                ProcessorMaxStatePercent = 72
            },
            AppliedAdvancedPowerSettings = appliedState
        });

        await manager.RollbackAsync();

        Assert.Equal(schemeGuid, powerPlanManager.RestoredAdvancedState?.SchemeGuid);
        Assert.Equal(PowerSourceMode.Battery, powerPlanManager.RestoredAdvancedState?.PowerSourceMode);
        Assert.Equal(72, powerPlanManager.RestoredAdvancedState?.ProcessorMaxStatePercent);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_PreservesPowerPlanStateWhenProviderReadbackDoesNotMatch()
    {
        var originalPlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: false);
        var appliedPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: true);
        var powerPlanManager = new FakePowerPlanManager
        {
            CurrentPowerPlan = appliedPlan,
            LeavePowerPlanUnchanged = true
        };
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousPowerPlan = originalPlan,
            AppliedPowerPlan = appliedPlan
        });

        await manager.RollbackAsync();

        Assert.True(powerPlanManager.SetActivePlanCalled);
        Assert.Equal(appliedPlan.SchemeGuid, powerPlanManager.CurrentPowerPlan?.SchemeGuid);
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_PreservesAdvancedStateWhenProviderReadbackDoesNotMatch()
    {
        var schemeGuid = Guid.NewGuid();
        var originalState = CreateAdvancedState(schemeGuid, 72);
        var appliedState = CreateAdvancedState(schemeGuid, 80);
        var powerPlanManager = new FakePowerPlanManager
        {
            CurrentAdvancedState = appliedState,
            LeaveAdvancedStateUnchanged = true
        };
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousAdvancedPowerSettings = originalState,
            AppliedAdvancedPowerSettings = appliedState
        });

        await manager.RollbackAsync();

        Assert.Equal(80, powerPlanManager.CurrentAdvancedState?.ProcessorMaxStatePercent);
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_DoesNotOverwritePowerPlanChangedAfterScheduling()
    {
        var originalPlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: false);
        var appliedPlan = new PowerPlanInfo(Guid.NewGuid(), "High Performance", IsActive: false);
        var userSelectedPlan = new PowerPlanInfo(Guid.NewGuid(), "Power Saver", IsActive: true);
        var powerPlanManager = new FakePowerPlanManager { CurrentPowerPlan = userSelectedPlan };
        var logger = new TestLogger();
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            new FakePriorityManager(failingProcessId: -1),
            logger);

        await manager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            PreviousPowerPlan = originalPlan,
            AppliedPowerPlan = appliedPlan
        });

        await manager.RollbackAsync();

        Assert.False(powerPlanManager.SetActivePlanCalled);
        Assert.False(File.Exists(_statePath));
        Assert.Contains(logger.Messages, message =>
            message.Contains("changed after scheduling", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RollbackAsync_DoesNotOverwriteAdvancedSettingsChangedAfterScheduling()
    {
        var schemeGuid = Guid.NewGuid();
        var originalState = CreateAdvancedState(schemeGuid, 100);
        var appliedState = CreateAdvancedState(schemeGuid, 80);
        var userState = CreateAdvancedState(schemeGuid, 65);
        var powerPlanManager = new FakePowerPlanManager { CurrentAdvancedState = userState };
        var logger = new TestLogger();
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            new FakePriorityManager(failingProcessId: -1),
            logger);

        await manager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            PreviousAdvancedPowerSettings = originalState,
            AppliedAdvancedPowerSettings = appliedState
        });

        await manager.RollbackAsync();

        Assert.Null(powerPlanManager.RestoredAdvancedState);
        Assert.False(File.Exists(_statePath));
        Assert.Contains(logger.Messages, message =>
            message.Contains("changed after scheduling", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CaptureAsync_MergesWithExistingUnresolvedState()
    {
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());
        var originalPlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: false);

        await manager.CaptureAsync(new SchedulerState
        {
            OriginalPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.Normal
            }
        });

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousPowerPlan = originalPlan,
            OriginalPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.High,
                [20] = PriorityLevel.BelowNormal
            }
        });

        var mergedState = await manager.GetLastKnownStateAsync();

        Assert.NotNull(mergedState);
        Assert.Equal(originalPlan.SchemeGuid, mergedState.PreviousPowerPlan?.SchemeGuid);
        Assert.Equal(PriorityLevel.Normal, mergedState.OriginalPriorities[10]);
        Assert.Equal(PriorityLevel.BelowNormal, mergedState.OriginalPriorities[20]);
    }

    [Fact]
    public async Task ConcurrentCaptures_DoNotLoseRollbackEntries()
    {
        var transactionId = Guid.NewGuid();
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());

        var captures = Enumerable.Range(1, 24)
            .Select(processId => manager.CaptureAsync(new SchedulerState
            {
                TransactionId = transactionId,
                OriginalPriorities = new Dictionary<int, PriorityLevel>
                {
                    [processId] = PriorityLevel.Normal
                }
            }))
            .ToArray();

        await Task.WhenAll(captures);

        var state = await manager.GetLastKnownStateAsync();
        Assert.NotNull(state);
        Assert.Equal(transactionId, state.TransactionId);
        Assert.Equal(24, state.OriginalPriorities.Count);
    }

    [Fact]
    public async Task CaptureAsync_PreservesOriginalAndUpdatesAppliedValueWithinTransaction()
    {
        var transactionId = Guid.NewGuid();
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            TransactionId = transactionId,
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.Normal },
            AppliedPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.BelowNormal }
        });
        await manager.CaptureAsync(new SchedulerState
        {
            TransactionId = transactionId,
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.High },
            AppliedPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.Idle }
        });

        var state = await manager.GetLastKnownStateAsync();

        Assert.NotNull(state);
        Assert.Equal(transactionId, state.TransactionId);
        Assert.Equal(PriorityLevel.Normal, state.OriginalPriorities[10]);
        Assert.Equal(PriorityLevel.Idle, state.AppliedPriorities[10]);
    }

    [Fact]
    public async Task CaptureAsync_RejectsStateFromDifferentTransaction()
    {
        var firstTransactionId = Guid.NewGuid();
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());
        await manager.CaptureAsync(new SchedulerState
        {
            TransactionId = firstTransactionId,
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.Normal }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CaptureAsync(new SchedulerState
        {
            TransactionId = Guid.NewGuid(),
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [20] = PriorityLevel.Normal }
        }));

        var state = await manager.GetLastKnownStateAsync();
        Assert.Equal(firstTransactionId, state?.TransactionId);
        Assert.False(state!.OriginalPriorities.ContainsKey(20));
    }

    [Fact]
    public async Task RollbackAsync_PreservesUnresolvedStateWhenPriorityRestoreFails()
    {
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(
            failingProcessId: 10,
            currentPriorities: new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.BelowNormal,
                [20] = PriorityLevel.Idle
            });
        var logger = new TestLogger();
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, logger);

        await manager.CaptureAsync(new SchedulerState
        {
            OriginalPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.Normal,
                [20] = PriorityLevel.BelowNormal
            },
            AppliedPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.BelowNormal,
                [20] = PriorityLevel.Idle
            },
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot>
            {
                [10] = CreateIdentity(10),
                [20] = CreateIdentity(20)
            }
        });

        await manager.RollbackAsync();

        var remainingState = await manager.GetLastKnownStateAsync();
        Assert.NotNull(remainingState);
        Assert.True(remainingState.OriginalPriorities.ContainsKey(10));
        Assert.False(remainingState.OriginalPriorities.ContainsKey(20));
        Assert.Contains(logger.Messages, message => message.Contains("remaining state was preserved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RollbackAsync_DropsLegacyPriorityStateWithoutTouchingProcess()
    {
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var logger = new TestLogger();
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            priorityManager,
            logger);

        await manager.CaptureAsync(new SchedulerState
        {
            OriginalPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.Normal
            }
        });

        await manager.RollbackAsync();

        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.False(File.Exists(_statePath));
        Assert.Contains(logger.Messages, message => message.Contains("legacy process rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RollbackAsync_RejectsCorruptRollbackStateWithoutDeletingIt()
    {
        await File.WriteAllTextAsync(_statePath, "{ this is not valid json");
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var logger = new TestLogger();
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, logger);

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.RollbackAsync());

        Assert.False(powerPlanManager.SetActivePlanCalled);
        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.True(File.Exists(_statePath));
        Assert.Contains(logger.Messages, message => message.Contains("remains protected", StringComparison.OrdinalIgnoreCase));

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.CaptureAsync(new SchedulerState()));
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_RejectsNullRollbackStateWithoutTreatingItAsEmpty()
    {
        await File.WriteAllTextAsync(_statePath, "null");
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            new FakePriorityManager(failingProcessId: -1),
            new TestLogger());

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.RollbackAsync());

        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_RejectsStructurallyInvalidStateWithoutTouchingProviders()
    {
        await File.WriteAllTextAsync(_statePath, "{\"originalPriorities\":null}");
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger());

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.RollbackAsync());

        Assert.False(powerPlanManager.SetActivePlanCalled);
        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_RejectsOrphanedAppliedEntriesWithoutDeletingState()
    {
        await File.WriteAllTextAsync(
            _statePath,
            "{\"appliedPriorities\":{\"10\":\"BelowNormal\"}}" );
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger());

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.RollbackAsync());

        Assert.False(powerPlanManager.SetActivePlanCalled);
        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_RejectsMismatchedProcessIdentityWithoutTouchingProviders()
    {
        await File.WriteAllTextAsync(
            _statePath,
            """
            {
              "originalPriorities": { "10": "Normal" },
              "appliedPriorities": { "10": "BelowNormal" },
              "processIdentities": {
                "10": {
                  "processId": 20,
                  "processName": "process-20",
                  "startedAt": "1970-01-01T00:00:00+00:00"
                }
              }
            }
            """);
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(
            failingProcessId: -1,
            currentPriorities: new Dictionary<int, PriorityLevel> { [20] = PriorityLevel.BelowNormal });
        var manager = new FileRollbackManager(
            _statePath,
            powerPlanManager,
            priorityManager,
            new TestLogger());

        await Assert.ThrowsAsync<RollbackStateUnavailableException>(() => manager.RollbackAsync());

        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_DropsStateWithoutTouchingReusedProcessId()
    {
        var identity = CreateIdentity(10);
        var priorityManager = new FakePriorityManager(
            failingProcessId: -1,
            mismatchedProcessIds: new HashSet<int> { 10 });
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            priorityManager,
            new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.Normal },
            AppliedPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.BelowNormal },
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [10] = identity }
        });

        await manager.RollbackAsync();

        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_DoesNotOverwritePriorityChangedAfterScheduling()
    {
        var priorityManager = new FakePriorityManager(
            failingProcessId: -1,
            currentPriorities: new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.High });
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            priorityManager,
            new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.Normal },
            AppliedPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.BelowNormal },
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [10] = CreateIdentity(10) }
        });

        await manager.RollbackAsync();

        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_RestoresPriorityAndEfficiencyModeWhenAppliedStateIsUnchanged()
    {
        var priorityManager = new FakePriorityManager(
            failingProcessId: -1,
            currentPriorities: new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.BelowNormal },
            currentEfficiencyModes: new Dictionary<int, bool> { [10] = true });
        var manager = new FileRollbackManager(
            _statePath,
            new FakePowerPlanManager(),
            priorityManager,
            new TestLogger());

        await manager.CaptureAsync(new SchedulerState
        {
            OriginalPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.Normal },
            AppliedPriorities = new Dictionary<int, PriorityLevel> { [10] = PriorityLevel.BelowNormal },
            ProcessIdentities = new Dictionary<int, ProcessIdentitySnapshot> { [10] = CreateIdentity(10) },
            OriginalEfficiencyModes = new Dictionary<int, ProcessEfficiencyModeState>
            {
                [10] = new() { IsEnabled = false }
            },
            AppliedEfficiencyModes = new Dictionary<int, bool> { [10] = true }
        });

        await manager.RollbackAsync();

        Assert.Equal(PriorityLevel.Normal, priorityManager.RestoredPriorities[10]);
        Assert.False(priorityManager.RestoredEfficiencyModes[10]);
        Assert.False(File.Exists(_statePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class FakePowerPlanManager : IPowerPlanManager
    {
        public bool SetActivePlanCalled { get; private set; }

        public PowerPlanAdvancedState? RestoredAdvancedState { get; private set; }

        public PowerPlanInfo? CurrentPowerPlan { get; set; }

        public PowerPlanAdvancedState? CurrentAdvancedState { get; set; }

        public bool LeavePowerPlanUnchanged { get; init; }

        public bool LeaveAdvancedStateUnchanged { get; init; }

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>(Array.Empty<PowerPlanInfo>());

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentPowerPlan);

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default)
        {
            SetActivePlanCalled = true;
            if (!LeavePowerPlanUnchanged)
            {
                CurrentPowerPlan = new PowerPlanInfo(schemeGuid, "Restored", IsActive: true);
            }

            return Task.FromResult(true);
        }

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                CurrentAdvancedState?.SchemeGuid == schemeGuid &&
                CurrentAdvancedState.PowerSourceMode == powerSourceMode
                    ? CurrentAdvancedState
                    : null);

        public Task<bool> ApplyAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            PowerPlanAdvancedPreference preference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RestoreAdvancedSettingsAsync(
            PowerPlanAdvancedState state,
            CancellationToken cancellationToken = default)
        {
            RestoredAdvancedState = state;
            if (!LeaveAdvancedStateUnchanged)
            {
                CurrentAdvancedState = state;
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FakePriorityManager(
        int failingProcessId,
        IReadOnlyDictionary<int, PriorityLevel>? currentPriorities = null,
        IReadOnlyDictionary<int, bool>? currentEfficiencyModes = null,
        IReadOnlySet<int>? mismatchedProcessIds = null) : IPriorityManager
    {
        public Dictionary<int, PriorityLevel> RestoredPriorities { get; } = new();

        public Dictionary<int, bool> RestoredEfficiencyModes { get; } = new();

        public Task<bool> IsSameProcessAsync(
            ProcessIdentitySnapshot identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(mismatchedProcessIds?.Contains(identity.ProcessId) != true);

        public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(
                currentPriorities is not null && currentPriorities.TryGetValue(processId, out var priority)
                    ? priority
                    : null);

        public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default)
        {
            if (processId == failingProcessId)
            {
                throw new InvalidOperationException("Process has exited.");
            }

            RestoredPriorities[processId] = priority;
            return Task.FromResult(true);
        }

        public Task<ProcessEfficiencyModeState?> TryGetEfficiencyModeAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessEfficiencyModeState?>(
                currentEfficiencyModes is not null && currentEfficiencyModes.TryGetValue(processId, out var enabled)
                    ? new ProcessEfficiencyModeState { IsEnabled = enabled }
                    : null);

        public Task<bool> SetEfficiencyModeAsync(
            int processId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            RestoredEfficiencyModes[processId] = enabled;
            return Task.FromResult(true);
        }

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult());

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

    private static ProcessIdentitySnapshot CreateIdentity(int processId) =>
        new()
        {
            ProcessId = processId,
            ProcessName = $"process-{processId}",
            StartedAt = DateTimeOffset.UnixEpoch
        };

    private static PowerPlanAdvancedState CreateAdvancedState(Guid schemeGuid, int maximumProcessorState) =>
        new()
        {
            SchemeGuid = schemeGuid,
            PowerSourceMode = PowerSourceMode.Ac,
            ProcessorMaxStatePercent = maximumProcessorState
        };

    private sealed class TestLogger : IAppLogger
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
