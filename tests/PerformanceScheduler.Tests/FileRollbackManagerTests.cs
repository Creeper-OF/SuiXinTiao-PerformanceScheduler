using PerformanceScheduler.Core.Abstractions;
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
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(failingProcessId: 10);
        var logger = new TestLogger();
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, logger);

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousPowerPlan = new PowerPlanInfo(Guid.NewGuid(), "Balanced", IsActive: false),
            OriginalPriorities = new Dictionary<int, PriorityLevel>
            {
                [10] = PriorityLevel.Normal,
                [20] = PriorityLevel.BelowNormal
            }
        });

        await manager.RollbackAsync();

        Assert.True(powerPlanManager.SetActivePlanCalled);
        Assert.Equal(PriorityLevel.BelowNormal, priorityManager.RestoredPriorities[20]);
        Assert.False(File.Exists(_statePath));
        Assert.Contains(logger.Messages, message => message.Contains("PID 10", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RollbackAsync_RestoresAdvancedPowerSettingsWhenCaptured()
    {
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, new TestLogger());
        var schemeGuid = Guid.NewGuid();

        await manager.CaptureAsync(new SchedulerState
        {
            PreviousAdvancedPowerSettings = new PowerPlanAdvancedState
            {
                SchemeGuid = schemeGuid,
                PowerSourceMode = PowerSourceMode.Battery,
                ProcessorMaxStatePercent = 72
            }
        });

        await manager.RollbackAsync();

        Assert.Equal(schemeGuid, powerPlanManager.RestoredAdvancedState?.SchemeGuid);
        Assert.Equal(PowerSourceMode.Battery, powerPlanManager.RestoredAdvancedState?.PowerSourceMode);
        Assert.Equal(72, powerPlanManager.RestoredAdvancedState?.ProcessorMaxStatePercent);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task RollbackAsync_IgnoresCorruptRollbackState()
    {
        await File.WriteAllTextAsync(_statePath, "{ this is not valid json");
        var powerPlanManager = new FakePowerPlanManager();
        var priorityManager = new FakePriorityManager(failingProcessId: -1);
        var logger = new TestLogger();
        var manager = new FileRollbackManager(_statePath, powerPlanManager, priorityManager, logger);

        await manager.RollbackAsync();

        Assert.False(powerPlanManager.SetActivePlanCalled);
        Assert.Empty(priorityManager.RestoredPriorities);
        Assert.Contains(logger.Messages, message => message.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
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

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>(Array.Empty<PowerPlanInfo>());

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanInfo?>(null);

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default)
        {
            SetActivePlanCalled = true;
            return Task.FromResult(true);
        }

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanAdvancedState?>(null);

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
            return Task.FromResult(true);
        }
    }

    private sealed class FakePriorityManager(int failingProcessId) : IPriorityManager
    {
        public Dictionary<int, PriorityLevel> RestoredPriorities { get; } = new();

        public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PriorityLevel?>(null);

        public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default)
        {
            if (processId == failingProcessId)
            {
                throw new InvalidOperationException("Process has exited.");
            }

            RestoredPriorities[processId] = priority;
            return Task.FromResult(true);
        }

        public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
            FocusedAppContext app,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PriorityAdjustmentResult());

        public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(Array.Empty<BackgroundProcessAdjustmentResult>());
    }

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
