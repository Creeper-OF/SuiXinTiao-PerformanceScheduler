using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class HardwareSelfTestServiceTests
{
    [Fact]
    public async Task GpuSelfTest_CanceledCapabilityRead_DoesNotReportUnsupportedSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var gpu = new FakeGpuTuningProvider
        {
            SupportsLock = false,
            SupportAction = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        var service = new HardwareSelfTestService(await CreateAcGateAsync(gpu), new FakePowerPlanManager(), new TestLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestGpuClockLockAndResetAsync(cancellationToken: cancellation.Token));

        Assert.Equal(0, gpu.SetCount);
        Assert.Equal(0, gpu.ResetCount);
    }

    [Fact]
    public async Task PowerQuery_CanceledReadReturningNoPlan_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var plan = new FakePowerPlanManager
        {
            QueryAction = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult<PowerPlanInfo?>(null);
            }
        };
        var service = new HardwareSelfTestService(await CreateAcGateAsync(new FakeGpuTuningProvider()), plan, new TestLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestPowerPlanQueryAsync(cancellation.Token));
    }

    [Fact]
    public async Task FullDiagnostic_OnBattery_ReportsSkippedGpuAndKeepsProtection()
    {
        var gpu = new FakeGpuTuningProvider();
        var gate = await CreateAcGateAsync(gpu);
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), new TestLogger());

        var result = await service.RunFullDiagnosticSelfTestAsync();

        Assert.Equal(SelfTestStatus.Warning, result.OverallStatus);
        Assert.True(result.GpuResult.Skipped);
        Assert.True(result.GpuResult.Supported);
        Assert.True(gpu.IsClockLocked);
        Assert.Equal(1, gpu.SetCount);
        Assert.Equal(0, gpu.ResetCount);
    }

    [Fact]
    public async Task GpuSelfTest_PowerChangesDuringCapabilityRead_SkipsWrite()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gpu = new FakeGpuTuningProvider
        {
            SupportAction = async token =>
            {
                entered.TrySetResult();
                await complete.Task.WaitAsync(token);
            }
        };
        var gate = await CreateAcGateAsync(gpu);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), new TestLogger());
        var diagnostic = service.TestGpuClockLockAndResetAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var battery = gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        complete.TrySetResult();

        Assert.True((await diagnostic.WaitAsync(TimeSpan.FromSeconds(5))).Skipped);
        Assert.True(await battery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, gpu.SetCount);
        Assert.Equal(0, gpu.ResetCount);
        Assert.True(gpu.IsClockLocked);
    }

    [Fact]
    public async Task GpuSelfTest_RecoveryFailureBlocksNewDiagnosticUntilRecovered()
    {
        var allowReset = false;
        var gpu = new FakeGpuTuningProvider { ResetAction = _ => Task.FromResult(allowReset) };
        var gate = await CreateAcGateAsync(gpu);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), new TestLogger());

        Assert.False((await service.TestGpuClockLockAndResetAsync()).IsSuccess);
        Assert.True((await service.TestGpuClockLockAndResetAsync()).Skipped);
        Assert.Equal(1, gpu.SetCount);
        Assert.True(gpu.IsClockLocked);

        allowReset = true;
        Assert.True(await gate.RecoverAsync());
        var retried = await service.TestGpuClockLockAndResetAsync();
        Assert.True(retried.IsSuccess);
        Assert.False(retried.Skipped);
        Assert.Equal(2, gpu.SetCount);
        Assert.False(gpu.IsClockLocked);
    }

    [Fact]
    public async Task Recovery_DuringGpuCleanup_WaitsForDiagnosticPair()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var gpu = new FakeGpuTuningProvider
        {
            ResetAction = async token =>
            {
                if (++attempt == 1)
                {
                    entered.TrySetResult();
                    await complete.Task.WaitAsync(token);
                }
                return true;
            }
        };
        var gate = await CreateAcGateAsync(gpu);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), new TestLogger());
        var diagnostic = service.TestGpuClockLockAndResetAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovery = gate.RecoverAsync();
        try
        {
            Assert.False(recovery.IsCompleted);
        }
        finally
        {
            complete.TrySetResult();
            await diagnostic.WaitAsync(TimeSpan.FromSeconds(5));
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(gpu.IsClockLocked);
    }

    [Theory]
    [InlineData(0u, 800u)]
    [InlineData(800u, 210u)]
    public async Task GpuSelfTest_InvalidRange_DoesNotWrite(uint min, uint max)
    {
        var gpu = new FakeGpuTuningProvider();
        var service = new HardwareSelfTestService(await CreateAcGateAsync(gpu), new FakePowerPlanManager(), new TestLogger());

        var result = await service.TestGpuClockLockAndResetAsync(min, max);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, gpu.SetCount);
        Assert.Equal(0, gpu.ResetCount);
    }

    [Fact]
    public async Task PowerQuery_WhenCallerCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var gpu = new FakeGpuTuningProvider();
        var plan = new FakePowerPlanManager
        {
            QueryAction = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<PowerPlanInfo?>(null);
            }
        };
        var service = new HardwareSelfTestService(await CreateAcGateAsync(gpu), plan, new TestLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestPowerPlanQueryAsync(cancellation.Token));
    }

    [Fact]
    public async Task ProcessSafety_WhenAlreadyCanceled_DoesNotReportSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new HardwareSelfTestService(await CreateAcGateAsync(new FakeGpuTuningProvider()),
            new FakePowerPlanManager(), new TestLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestProcessSafetyRulesAsync(cancellation.Token));
    }

    [Fact]
    public async Task EmergencySimulation_DoesNotReleaseLiveBatteryProtection()
    {
        var gpu = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(gpu, logger);
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), logger);

        var result = await service.TestPowerEmergencyGateSimulationAsync();

        Assert.True(result.IsSuccess);
        Assert.True(gate.IsThrottled);
        Assert.True(gpu.IsClockLocked);
        Assert.False(gpu.ResetCalled);
    }

    [Fact]
    public async Task GpuSelfTest_WhenBatteryProtectionIsActive_DoesNotWriteOrReset()
    {
        var gpu = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(gpu, logger);
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), logger);

        await service.TestGpuClockLockAndResetAsync();

        Assert.Equal(1, gpu.SetCount);
        Assert.False(gpu.ResetCalled);
        Assert.True(gpu.IsClockLocked);
    }

    [Fact]
    public async Task GpuSelfTest_WhenPowerSourceIsUnknown_DoesNotWrite()
    {
        var gpu = new FakeGpuTuningProvider();
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(gpu, logger);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), logger);

        await service.TestGpuClockLockAndResetAsync();

        Assert.Equal(0, gpu.SetCount);
        Assert.False(gpu.ResetCalled);
    }

    [Fact]
    public async Task GpuSelfTest_WhenBatteryClampFailed_DoesNotConsumeItsRecovery()
    {
        var gpu = new FakeGpuTuningProvider { SetSucceeds = false };
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(gpu, logger);
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), logger);

        await service.TestGpuClockLockAndResetAsync();

        Assert.Equal(1, gpu.SetCount);
        Assert.False(gpu.ResetCalled);
    }

    [Fact]
    public async Task GpuSelfTest_BatteryArrivingDuringLock_IsAppliedAfterTestCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var gpu = new FakeGpuTuningProvider
        {
            SetAction = async token =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    entered.TrySetResult();
                    await complete.Task.WaitAsync(token);
                }
                return true;
            }
        };
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(gpu, logger);
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Any, PowerSourceMode.Ac);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), logger);

        var selfTest = service.TestGpuClockLockAndResetAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var battery = gate.HandlePowerTransitionAsync(PowerSourceMode.Ac, PowerSourceMode.Battery);
        try
        {
            Assert.False(battery.IsCompleted);
        }
        finally
        {
            complete.TrySetResult();
            await selfTest.WaitAsync(TimeSpan.FromSeconds(5));
            await battery.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(gate.IsThrottled);
        Assert.True(gpu.IsClockLocked);
        Assert.Equal(2, gpu.SetCount);
    }

    [Fact]
    public async Task GpuSelfTest_WhenCallerCancels_RecoversBeforePropagatingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var gpu = new FakeGpuTuningProvider
        {
            SetAction = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            }
        };
        var logger = new TestLogger();
        var gate = new PowerEmergencyGate(gpu, logger);
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Any, PowerSourceMode.Ac);
        var service = new HardwareSelfTestService(gate, new FakePowerPlanManager(), logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestGpuClockLockAndResetAsync(cancellationToken: cancellation.Token));

        Assert.True(gpu.ResetCalled);
        Assert.False(gpu.ResetToken.CanBeCanceled);
        Assert.False(gpu.IsClockLocked);
        Assert.Equal(1, gpu.ResetCount);
    }

    [Fact]
    public async Task TestGpuClockLockAndResetAsync_WhenLockReportsFailure_StillAttemptsRecovery()
    {
        var gpu = new FakeGpuTuningProvider { SetSucceeds = false };
        var service = new HardwareSelfTestService(await CreateAcGateAsync(gpu), new FakePowerPlanManager(), new TestLogger());

        var result = await service.TestGpuClockLockAndResetAsync();

        Assert.False(result.IsSuccess);
        Assert.True(gpu.ResetCalled);
    }

    [Fact]
    public async Task TestGpuClockLockAndResetAsync_RecoveryDoesNotUseCallerCancellation()
    {
        var gpu = new FakeGpuTuningProvider();
        var service = new HardwareSelfTestService(await CreateAcGateAsync(gpu), new FakePowerPlanManager(), new TestLogger());
        using var cancellation = new CancellationTokenSource();

        var result = await service.TestGpuClockLockAndResetAsync(cancellationToken: cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.False(gpu.ResetToken.CanBeCanceled);
    }

    [Fact]
    public async Task TestGpuClockLockAndResetAsync_WhenSupportQueryFails_DoesNotResetExistingHardwareState()
    {
        var gpu = new FakeGpuTuningProvider { SupportQueryFails = true };
        var service = new HardwareSelfTestService(await CreateAcGateAsync(gpu), new FakePowerPlanManager(), new TestLogger());

        var result = await service.TestGpuClockLockAndResetAsync();

        Assert.False(result.IsSuccess);
        Assert.False(gpu.ResetCalled);
    }

    [Fact]
    public async Task TestGpuClockLockAndResetAsync_WhenSupported_PerformsLockAndReset()
    {
        var fakeGpu = new FakeGpuTuningProvider { SupportsLock = true };
        var fakePowerPlan = new FakePowerPlanManager();
        var logger = new TestLogger();

        var service = new HardwareSelfTestService(await CreateAcGateAsync(fakeGpu), fakePowerPlan, logger);
        var result = await service.TestGpuClockLockAndResetAsync(210, 800);

        Assert.True(result.IsSuccess);
        Assert.True(result.Supported);
        Assert.Equal(210u, fakeGpu.LastMinClock);
        Assert.Equal(800u, fakeGpu.LastMaxClock);
        Assert.True(fakeGpu.ResetCalled);
    }

    [Fact]
    public async Task TestGpuClockLockAndResetAsync_WhenUnsupported_GracefullyReturnsUnsupported()
    {
        var fakeGpu = new FakeGpuTuningProvider { SupportsLock = false };
        var fakePowerPlan = new FakePowerPlanManager();
        var logger = new TestLogger();

        var service = new HardwareSelfTestService(await CreateAcGateAsync(fakeGpu), fakePowerPlan, logger);
        var result = await service.TestGpuClockLockAndResetAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Supported);
        Assert.Null(fakeGpu.LastMinClock);
    }

    [Fact]
    public async Task TestPowerEmergencyGateSimulationAsync_Success()
    {
        var fakeGpu = new FakeGpuTuningProvider();
        var fakePowerPlan = new FakePowerPlanManager();
        var logger = new TestLogger();

        var service = new HardwareSelfTestService(await CreateAcGateAsync(fakeGpu), fakePowerPlan, logger);
        var result = await service.TestPowerEmergencyGateSimulationAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.ClampedSuccessfully);
        Assert.True(result.ReleasedSuccessfully);
        Assert.Null(fakeGpu.LastMinClock);
        Assert.False(fakeGpu.ResetCalled);
    }

    [Fact]
    public async Task TestPowerPlanQueryAsync_Success()
    {
        var fakeGpu = new FakeGpuTuningProvider();
        var fakePowerPlan = new FakePowerPlanManager();
        var logger = new TestLogger();

        var service = new HardwareSelfTestService(await CreateAcGateAsync(fakeGpu), fakePowerPlan, logger);
        var result = await service.TestPowerPlanQueryAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Balanced Plan", result.ActivePlanName);
        Assert.Equal(100, result.MaxProcessorStateAc);
        Assert.Equal(80, result.MaxProcessorStateDc);
    }

    [Fact]
    public async Task TestProcessSafetyRulesAsync_VerifiesRules()
    {
        var fakeGpu = new FakeGpuTuningProvider();
        var fakePowerPlan = new FakePowerPlanManager();
        var logger = new TestLogger();

        var service = new HardwareSelfTestService(await CreateAcGateAsync(fakeGpu), fakePowerPlan, logger);
        var result = await service.TestProcessSafetyRulesAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.RealTimeBlocked);
        Assert.True(result.SelfProcessBlocked);
        Assert.True(result.ProtectedSystemProcessesBlocked);
    }

    [Fact]
    public async Task RunFullDiagnosticSelfTestAsync_AggregatesResults()
    {
        var fakeGpu = new FakeGpuTuningProvider { SupportsLock = true };
        var fakePowerPlan = new FakePowerPlanManager();
        var logger = new TestLogger();

        var service = new HardwareSelfTestService(await CreateAcGateAsync(fakeGpu), fakePowerPlan, logger);
        var result = await service.RunFullDiagnosticSelfTestAsync();

        Assert.Equal(SelfTestStatus.Passed, result.OverallStatus);
        Assert.True(result.GpuResult.IsSuccess);
        Assert.True(result.EmergencyGateResult.IsSuccess);
        Assert.True(result.PowerPlanResult.IsSuccess);
        Assert.True(result.ProcessSafetyResult.IsSuccess);
        Assert.Contains("Self-test completed", result.Summary);
    }

    private sealed class FakeGpuTuningProvider : IGpuTuningProvider
    {
        public GpuVendor Vendor => GpuVendor.Nvidia;
        public string ProviderName => "Fake Nvidia";
        public bool SupportsLock { get; init; } = true;
        public bool SetSucceeds { get; init; } = true;
        public bool SupportQueryFails { get; init; }
        public Func<CancellationToken, Task<bool>>? SetAction { get; init; }
        public Func<CancellationToken, Task>? SupportAction { get; init; }
        public Func<CancellationToken, Task<bool>>? ResetAction { get; init; }
        public int SetCount { get; private set; }
        public int ResetCount { get; private set; }
        public bool IsClockLocked { get; private set; }
        public CancellationToken ResetToken { get; private set; }
        public uint? LastMinClock { get; private set; }
        public uint? LastMaxClock { get; private set; }
        public bool ResetCalled { get; private set; }

        public async Task<GpuTuningProviderSupport> GetSupportAsync(
            GpuCapabilitySnapshot detectedGpu,
            CancellationToken cancellationToken = default)
        {
            if (SupportAction is not null) await SupportAction(cancellationToken);
            if (SupportQueryFails)
            {
                throw new IOException("GPU support query failed");
            }

            var features = SupportsLock
                ? new[] { GpuTuningFeature.Telemetry, GpuTuningFeature.CoreClockLimit, GpuTuningFeature.ResetToDefault }
                : new[] { GpuTuningFeature.Telemetry };

            return new GpuTuningProviderSupport
            {
                ProviderName = ProviderName,
                SupportsApplyPipeline = SupportsLock,
                SupportedFeatures = features
            };
        }

        public async Task<bool> TrySetLockedClocksAsync(
            uint minGpuClockMHz,
            uint maxGpuClockMHz,
            CancellationToken cancellationToken = default)
        {
            SetCount++;
            LastMinClock = minGpuClockMHz;
            LastMaxClock = maxGpuClockMHz;
            var success = SetAction is null ? SetSucceeds : await SetAction(cancellationToken);
            IsClockLocked |= success;
            return success;
        }

        public async Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default)
        {
            ResetCalled = true;
            ResetToken = cancellationToken;
            ResetCount++;
            var success = ResetAction is null || await ResetAction(cancellationToken);
            if (success) IsClockLocked = false;
            return success;
        }
    }

    private static async Task<PowerEmergencyGate> CreateAcGateAsync(IGpuTuningProvider gpu)
    {
        var gate = new PowerEmergencyGate(gpu, new TestLogger());
        await gate.HandlePowerTransitionAsync(PowerSourceMode.Any, PowerSourceMode.Ac);
        return gate;
    }

    private sealed class FakePowerPlanManager : IPowerPlanManager
    {
        public Guid ActiveGuid { get; } = Guid.NewGuid();
        public Func<CancellationToken, Task<PowerPlanInfo?>>? QueryAction { get; init; }

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerPlanInfo>>([
                new PowerPlanInfo(ActiveGuid, "Balanced Plan", IsActive: true)
            ]);

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            QueryAction?.Invoke(cancellationToken) ??
            Task.FromResult<PowerPlanInfo?>(new PowerPlanInfo(ActiveGuid, "Balanced Plan", IsActive: true));

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default)
        {
            var maxCpu = powerSourceMode == PowerSourceMode.Ac ? 100 : 80;
            return Task.FromResult<PowerPlanAdvancedState?>(new PowerPlanAdvancedState
            {
                SchemeGuid = schemeGuid,
                PowerSourceMode = powerSourceMode,
                ProcessorMaxStatePercent = maxCpu
            });
        }

        public Task<bool> ApplyAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            PowerPlanAdvancedPreference preference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RestoreAdvancedSettingsAsync(
            PowerPlanAdvancedState state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestLogger : IAppLogger
    {
#pragma warning disable CS0067
        public event EventHandler<LogEntry>? EntryWritten;
#pragma warning restore CS0067

        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) => Array.Empty<LogEntry>();

        public void Trace(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public void PublishUserEvent(UserEventCode code, AppLogLevel level, string message, params object[] args) { }
    }
}
