using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class PowerEmergencyGateTests
{
    [Fact]
    public async Task UnknownPowerObservation_KeepsExistingClamp()
    {
        var provider = new FakeGpuTuningProvider();
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);

        await gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Any);

        Assert.True(gate.IsThrottled);
        Assert.False(provider.ResetCalled);
    }

    [Fact]
    public async Task SameBatteryObservation_AfterManualRecovery_ReappliesClamp()
    {
        var attempts = 0;
        var provider = new FakeGpuTuningProvider { SetAction = _ => Task.FromResult(++attempts > 0) };
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        Assert.True(await gate.RecoverAsync());

        Assert.True(await gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Battery));

        Assert.True(gate.IsThrottled);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task BatteryAfterFailedReset_ReappliesClampBecauseSomeDevicesMayHaveBeenReleased()
    {
        var attempts = 0;
        var provider = new FakeGpuTuningProvider
        {
            SetAction = _ => Task.FromResult(++attempts > 0),
            ResetAction = _ => Task.FromResult(false)
        };
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        Assert.False(await gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Ac));

        Assert.True(await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery));

        Assert.Equal(2, attempts);
        Assert.True(gate.IsThrottled);
    }

    [Fact]
    public async Task RepeatedBatteryObservation_RetriesFailedClamp()
    {
        var attempt = 0;
        var provider = new FakeGpuTuningProvider
        {
            SetAction = _ => Task.FromResult(++attempt > 1)
        };
        var gate = new PowerEmergencyGate(provider, new TestLogger());

        Assert.False(await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery));
        Assert.True(await gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Battery));

        Assert.Equal(2, attempt);
        Assert.True(gate.IsThrottled);
    }

    [Fact]
    public async Task RepeatedAcObservation_RetriesFailedReset()
    {
        var attempt = 0;
        var provider = new FakeGpuTuningProvider
        {
            ResetAction = _ => Task.FromResult(++attempt > 1)
        };
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);

        Assert.False(await gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Ac));
        Assert.False(gate.IsThrottled);
        Assert.True(await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Ac));

        Assert.Equal(2, attempt);
        Assert.False(gate.IsThrottled);
    }

    [Fact]
    public async Task AcAfterFailedBatteryClamp_ResetsEvenIfPreviousModeWasAc()
    {
        var provider = new FakeGpuTuningProvider { SetClocksSucceeds = false };
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);

        Assert.True(await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Ac));

        Assert.True(provider.ResetCalled);
    }

    [Fact]
    public async Task ReleaseAsync_WhenClampReportsFailure_StillAttemptsRecovery()
    {
        var provider = new FakeGpuTuningProvider { SetClocksSucceeds = false };
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        Assert.False(await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery));

        Assert.True(await gate.ReleaseAsync());

        Assert.True(provider.ResetCalled);
    }

    [Fact]
    public async Task HandlePowerTransitionAsync_AcReconnectDuringInFlightClamp_WaitsThenReleases()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeGpuTuningProvider
        {
            SetAction = _ =>
            {
                entered.SetResult();
                return complete.Task;
            }
        };
        var gate = new PowerEmergencyGate(provider, new TestLogger());
        var clamp = gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var release = gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Ac);
        try
        {
            Assert.False(release.IsCompleted);
        }
        finally
        {
            complete.TrySetResult(true);
        }

        Assert.True(await clamp);
        Assert.True(await release);
        Assert.False(gate.IsThrottled);
        Assert.True(provider.ResetCalled);
    }

    [Fact]
    public async Task HandlePowerTransitionAsync_AcToBattery_AppliesClockClamp()
    {
        var fakeProvider = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(fakeProvider, logger, emergencyMinClockMHz: 210, emergencyMaxClockMHz: 800);

        var result = await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);

        Assert.True(result);
        Assert.True(gate.IsThrottled);
        Assert.Equal(210u, fakeProvider.LastMinClock);
        Assert.Equal(800u, fakeProvider.LastMaxClock);
        Assert.Contains(logger.Messages, msg => msg.Contains("Requesting GPU clock range"));
    }

    [Fact]
    public async Task HandlePowerTransitionAsync_BatteryToAc_ReleasesClockClamp()
    {
        var fakeProvider = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(fakeProvider, logger);

        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        Assert.True(gate.IsThrottled);

        var result = await gate.HandlePowerTransitionAsync(PowerSourceMode.Battery, PowerSourceMode.Ac);

        Assert.True(result);
        Assert.False(gate.IsThrottled);
        Assert.True(fakeProvider.ResetCalled);
        Assert.Contains(logger.Messages, msg => msg.Contains("Releasing GPU emergency clock clamp"));
    }

    [Fact]
    public async Task HandlePowerTransitionAsync_SameMode_DoesNotTriggerClampOrReset()
    {
        var fakeProvider = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(fakeProvider, logger);

        var result = await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Ac);

        Assert.True(result);
        Assert.False(gate.IsThrottled);
        Assert.Null(fakeProvider.LastMinClock);
        Assert.False(fakeProvider.ResetCalled);
    }

    [Fact]
    public async Task ReleaseAsync_WhenThrottled_ResetsClocks()
    {
        var fakeProvider = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(fakeProvider, logger);

        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        Assert.True(gate.IsThrottled);

        var result = await gate.ReleaseAsync();

        Assert.True(result);
        Assert.False(gate.IsThrottled);
        Assert.True(fakeProvider.ResetCalled);
    }

    [Fact]
    public async Task ReleaseAsync_WhenNotThrottled_IsNoOp()
    {
        var fakeProvider = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(fakeProvider, logger);

        var result = await gate.ReleaseAsync();

        Assert.True(result);
        Assert.False(fakeProvider.ResetCalled);
    }

    [Fact]
    public async Task HandlePowerTransitionAsync_WhenGpuProviderFails_ReturnsFalse()
    {
        var fakeProvider = new FakeGpuTuningProvider { SetClocksSucceeds = false };
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(fakeProvider, logger);

        var result = await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);

        Assert.False(result);
        Assert.False(gate.IsThrottled);
        Assert.Contains(logger.Messages, msg => msg.Contains("Failed to apply GPU emergency clock clamp"));
    }

    private sealed class FakeGpuTuningProvider : IGpuTuningProvider
    {
        public GpuVendor Vendor => GpuVendor.Nvidia;
        public string ProviderName => "Fake Nvidia";

        public bool SetClocksSucceeds { get; init; } = true;
        public Func<CancellationToken, Task<bool>>? SetAction { get; init; }
        public Func<CancellationToken, Task<bool>>? ResetAction { get; init; }
        public uint? LastMinClock { get; private set; }
        public uint? LastMaxClock { get; private set; }
        public bool ResetCalled { get; private set; }

        public Task<GpuTuningProviderSupport> GetSupportAsync(
            GpuCapabilitySnapshot detectedGpu,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GpuTuningProviderSupport());

        public Task<bool> TrySetLockedClocksAsync(
            uint minGpuClockMHz,
            uint maxGpuClockMHz,
            CancellationToken cancellationToken = default)
        {
            LastMinClock = minGpuClockMHz;
            LastMaxClock = maxGpuClockMHz;
            return SetAction?.Invoke(cancellationToken) ?? Task.FromResult(SetClocksSucceeds);
        }

        public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default)
        {
            ResetCalled = true;
            return ResetAction?.Invoke(cancellationToken) ?? Task.FromResult(true);
        }
    }

    private sealed class TestLogger : IAppLogger
    {
        public List<string> Messages { get; } = new();

#pragma warning disable CS0067
        public event EventHandler<LogEntry>? EntryWritten;
#pragma warning restore CS0067

        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) => Array.Empty<LogEntry>();

        public void Trace(string message) => Messages.Add($"TRACE: {message}");

        public void Info(string message) => Messages.Add($"INFO: {message}");

        public void Warn(string message) => Messages.Add($"WARN: {message}");

        public void Error(string message, Exception? exception = null) => Messages.Add($"ERROR: {message}");

        public void PublishUserEvent(UserEventCode code, AppLogLevel level, string message, params object[] args) { }
    }
}
