using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Infrastructure.Diagnostics;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class GpuClockRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PerformanceScheduler.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly FileGpuClockRecoveryStore _store;

    public GpuClockRecoveryTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "gpu-clock-recovery.json");
        _store = new FileGpuClockRecoveryStore(_path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateRecovery_ResetsOnlyPersistedApplicationChanges(bool pendingFromPreviousSession)
    {
        if (pendingFromPreviousSession)
        {
            await _store.MarkPendingAsync();
        }
        var source = new ControlledNvmlSource();
        var provider = new NvidiaGpuTuningProvider(source, new FileGpuClockRecoveryStore(_path));
        var gate = new PowerEmergencyGate(provider, new FileAppLogger(Path.Combine(_directory, "logs")));

        Assert.True(await gate.RecoverAsync());

        Assert.Equal(pendingFromPreviousSession, source.ResetCalled);
        Assert.False(await _store.IsPendingAsync());
    }

    [Fact]
    public async Task TrySetLockedClocksAsync_CommitsRecoveryMarkerBeforeHardwareWrite()
    {
        var source = new ControlledNvmlSource
        {
            SetAction = async _ =>
            {
                Assert.True(await new FileGpuClockRecoveryStore(_path).IsPendingAsync());
                return true;
            }
        };
        var provider = new NvidiaGpuTuningProvider(source, _store);

        Assert.True(await provider.TrySetLockedClocksAsync(210, 800));

        Assert.True(await _store.IsPendingAsync());
    }

    [Fact]
    public async Task TrySetLockedClocksAsync_WhenHardwareReportsFailure_PreservesRecoveryMarker()
    {
        var source = new ControlledNvmlSource { SetAction = _ => Task.FromResult(false) };
        var provider = new NvidiaGpuTuningProvider(source, _store);

        Assert.False(await provider.TrySetLockedClocksAsync(210, 800));

        Assert.True(await _store.IsPendingAsync());
    }

    [Fact]
    public async Task TryResetLockedClocksAsync_WhenHardwareReportsFailure_PreservesRecoveryMarker()
    {
        var source = new ControlledNvmlSource { ResetSucceeds = false };
        var provider = new NvidiaGpuTuningProvider(source, _store);
        await provider.TrySetLockedClocksAsync(210, 800);

        Assert.False(await provider.TryResetLockedClocksAsync());

        Assert.True(await _store.IsPendingAsync());
    }

    [Fact]
    public async Task TryResetLockedClocksAsync_AfterRestart_RemovesMarkerAfterSuccessfulReset()
    {
        await _store.MarkPendingAsync();
        var source = new ControlledNvmlSource
        {
            ResetAction = async _ =>
            {
                Assert.True(await _store.IsPendingAsync());
                return true;
            }
        };
        var restarted = new NvidiaGpuTuningProvider(source, new FileGpuClockRecoveryStore(_path));

        Assert.True(await restarted.TryResetLockedClocksAsync());

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task TryResetLockedClocksAsync_WithoutOwnedMarker_DoesNotChangeHardware()
    {
        var source = new ControlledNvmlSource();
        var provider = new NvidiaGpuTuningProvider(source, _store);

        Assert.True(await provider.TryResetLockedClocksAsync());

        Assert.False(source.ResetCalled);
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData(0u, 800u)]
    [InlineData(900u, 800u)]
    [InlineData(210u, 0u)]
    public async Task TrySetLockedClocksAsync_WithInvalidRange_BlocksHardwareWrite(uint minimum, uint maximum)
    {
        var source = new ControlledNvmlSource();
        var provider = new NvidiaGpuTuningProvider(source, _store);

        Assert.False(await provider.TrySetLockedClocksAsync(minimum, maximum));

        Assert.False(source.SetCalled);
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData("broken json")]
    [InlineData("{\"schemaVersion\":2,\"resetRequired\":true}")]
    [InlineData("{\"schemaVersion\":1,\"resetRequired\":false}")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"1\",\"resetRequired\":true}")]
    [InlineData("{\"schemaVersion\":null,\"resetRequired\":true}")]
    public async Task CorruptRecoveryMarker_BlocksWritesAndResetWithoutOverwritingEvidence(string contents)
    {
        await File.WriteAllTextAsync(_path, contents);
        var source = new ControlledNvmlSource();
        var provider = new NvidiaGpuTuningProvider(source, _store);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.TrySetLockedClocksAsync(210, 800));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.TryResetLockedClocksAsync());

        Assert.Equal(contents, await File.ReadAllTextAsync(_path));
        Assert.False(source.SetCalled);
        Assert.False(source.ResetCalled);
    }

    [Fact]
    public async Task TrySetLockedClocksAsync_WhenMarkerCannotBeWritten_BlocksHardwareWrite()
    {
        Directory.CreateDirectory(_path);
        var source = new ControlledNvmlSource();
        var provider = new NvidiaGpuTuningProvider(source, _store);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.TrySetLockedClocksAsync(210, 800));

        Assert.False(source.SetCalled);
    }

    [Fact]
    public async Task TrySetLockedClocksAsync_WhenCancelledDuringHardwareWrite_LeavesMarkerForUncancelledRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ControlledNvmlSource
        {
            SetAction = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<bool>(token);
            }
        };
        var provider = new NvidiaGpuTuningProvider(source, _store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TrySetLockedClocksAsync(210, 800, cancellation.Token));
        Assert.True(await _store.IsPendingAsync());

        Assert.True(await provider.TryResetLockedClocksAsync(CancellationToken.None));
        Assert.False(await _store.IsPendingAsync());
    }

    [Fact]
    public async Task TryResetLockedClocksAsync_WaitsForInFlightWriteBeforeClearingMarker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledNvmlSource
        {
            SetAction = _ =>
            {
                entered.SetResult();
                return complete.Task;
            }
        };
        var provider = new NvidiaGpuTuningProvider(source, _store);
        var write = provider.TrySetLockedClocksAsync(210, 800);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reset = provider.TryResetLockedClocksAsync();
        try
        {
            Assert.False(source.ResetCalled);
        }
        finally
        {
            complete.TrySetResult(true);
        }

        Assert.True(await write);
        Assert.True(await reset);
        Assert.False(await _store.IsPendingAsync());
    }

    public void Dispose() => TestDirectory.DeleteWithRetry(_directory);

    private sealed class ControlledNvmlSource : INvidiaNvmlSource
    {
        public Func<CancellationToken, Task<bool>>? SetAction { get; init; }
        public Func<CancellationToken, Task<bool>>? ResetAction { get; init; }
        public bool ResetSucceeds { get; init; } = true;
        public bool SetCalled { get; private set; }
        public bool ResetCalled { get; private set; }

        public Task<NvidiaNvmlAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NvidiaNvmlAvailability { IsAvailable = true, DeviceCount = 1, SupportsClockLocking = true });

        public Task<NvidiaNvmlSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NvidiaNvmlSnapshot());

        public Task<bool> TrySetLockedClocksAsync(uint minimum, uint maximum, CancellationToken cancellationToken = default)
        {
            SetCalled = true;
            return SetAction?.Invoke(cancellationToken) ?? Task.FromResult(true);
        }

        public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default)
        {
            ResetCalled = true;
            return ResetAction?.Invoke(cancellationToken) ?? Task.FromResult(ResetSucceeds);
        }
    }
}
