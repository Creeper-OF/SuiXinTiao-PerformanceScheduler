using System.Collections.Concurrent;
using System.Threading.Channels;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Power;

namespace PerformanceScheduler.Tests;

public sealed class Win32PowerChangeWatcherTests
{
    [Fact]
    public async Task FailedAction_UsesRetryIntervalButPowerChangesBypassIt()
    {
        var provider = new ControlledPowerStatusProvider();
        provider.Enqueue(PowerSourceMode.Ac);
        provider.Enqueue(PowerSourceMode.Battery);
        var clock = new ManualTimestampProvider();
        var attempts = 0;
        var gate = new FakeEmergencyGate
        {
            HandleAction = (_, current) =>
            {
                if (current != PowerSourceMode.Battery) return true;
                attempts++;
                return false;
            }
        };
        await using var watcher = new Win32PowerChangeWatcher(provider, gate, new TestLogger(),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), clock);

        watcher.Start();
        await provider.WaitForReadAsync(3);
        provider.Enqueue(PowerSourceMode.Battery);
        await provider.WaitForReadAsync(4);
        Assert.Equal(1, attempts);

        clock.Advance(TimeSpan.FromSeconds(1));
        provider.Enqueue(PowerSourceMode.Battery);
        await provider.WaitForReadAsync(5);
        Assert.Equal(2, attempts);

        provider.Enqueue(PowerSourceMode.Ac);
        await provider.WaitForReadAsync(6);
        Assert.Contains(gate.Transitions, item =>
            item.Previous == PowerSourceMode.Battery && item.Current == PowerSourceMode.Ac);
    }

    [Fact]
    public async Task ConcurrentStarts_UseOneMonitoringLoop()
    {
        var provider = new ControlledPowerStatusProvider();
        await using var watcher = CreateWatcher(provider, new FakeEmergencyGate());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(watcher.Start)));
        await provider.WaitForReadAsync(1);

        Assert.Equal(1, provider.ReadCount);
    }

    [Fact]
    public async Task Disposal_CancelsPendingReadAndPreventsRestart()
    {
        var provider = new ControlledPowerStatusProvider();
        var gate = new FakeEmergencyGate();
        var watcher = CreateWatcher(provider, gate);
        watcher.Start();
        await provider.WaitForReadAsync(1);

        await Task.WhenAll(watcher.DisposeAsync().AsTask(), watcher.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<ObjectDisposedException>(watcher.Start);
        Assert.Empty(gate.Transitions);
    }

    [Fact]
    public async Task StartingOnBattery_AppliesProtectionBeforeNextSample()
    {
        var provider = new ControlledPowerStatusProvider();
        provider.Enqueue(PowerSourceMode.Battery);
        var gate = new FakeEmergencyGate();
        await using var watcher = CreateWatcher(provider, gate);

        watcher.Start();
        await provider.WaitForReadAsync(2);

        Assert.Contains(gate.Transitions, transition => transition.Current == PowerSourceMode.Battery);
    }

    [Fact]
    public async Task FailedTransition_IsRetriedWithoutAnotherPowerChange()
    {
        var provider = new ControlledPowerStatusProvider { Fallback = PowerSourceMode.Battery };
        provider.Enqueue(PowerSourceMode.Ac);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var gate = new FakeEmergencyGate
        {
            HandleAction = (_, current) =>
            {
                if (current != PowerSourceMode.Battery) return true;
                if (++attempts == 1) return false;
                recovered.TrySetResult();
                return true;
            }
        };
        await using var watcher = CreateWatcher(provider, gate);

        watcher.Start();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(attempts >= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerQueryFailure_DoesNotTerminateMonitoring(bool independentCancellation)
    {
        var provider = new ControlledPowerStatusProvider();
        provider.EnqueueFailure(independentCancellation
            ? new OperationCanceledException("provider canceled independently")
            : new IOException("temporary power read failure"));
        provider.Enqueue(PowerSourceMode.Battery);
        var gate = new FakeEmergencyGate();
        await using var watcher = CreateWatcher(provider, gate);

        watcher.Start();
        await provider.WaitForReadAsync(3);

        Assert.Contains(gate.Transitions, transition => transition.Current == PowerSourceMode.Battery);
    }

    [Fact]
    public async Task GateFailure_DoesNotTerminateMonitoring()
    {
        var provider = new ControlledPowerStatusProvider { Fallback = PowerSourceMode.Battery };
        provider.Enqueue(PowerSourceMode.Ac);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var gate = new FakeEmergencyGate
        {
            HandleAction = (_, current) =>
            {
                if (current != PowerSourceMode.Battery) return true;
                if (++attempts == 1) throw new IOException("temporary driver failure");
                completed.TrySetResult();
                return true;
            }
        };
        await using var watcher = CreateWatcher(provider, gate);

        watcher.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(attempts >= 2);
    }

    private static Win32PowerChangeWatcher CreateWatcher(IPowerStatusProvider provider, IPowerEmergencyGate gate) =>
        new(provider, gate, new TestLogger(), TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task PowerChangeWatcher_DetectsTransitionAndForwardsToGate()
    {
        var provider = new ControlledPowerStatusProvider();
        provider.Enqueue(PowerSourceMode.Ac);
        provider.Enqueue(PowerSourceMode.Battery);
        var gate = new FakeEmergencyGate();
        await using var watcher = CreateWatcher(provider, gate);

        watcher.Start();
        await provider.WaitForReadAsync(3);

        Assert.Contains(gate.Transitions, transition =>
            transition.Previous == PowerSourceMode.Ac && transition.Current == PowerSourceMode.Battery);
    }

    private sealed class FakeEmergencyGate : IPowerEmergencyGate
    {
        public bool IsThrottled { get; set; }
        public ConcurrentQueue<(PowerSourceMode Previous, PowerSourceMode Current)> Transitions { get; } = new();
        public Func<PowerSourceMode, PowerSourceMode, bool>? HandleAction { get; init; }

        public Task<bool> HandlePowerTransitionAsync(
            PowerSourceMode previousMode,
            PowerSourceMode currentMode,
            CancellationToken cancellationToken = default)
        {
            Transitions.Enqueue((previousMode, currentMode));
            return Task.FromResult(HandleAction?.Invoke(previousMode, currentMode) ?? true);
        }

        public Task<bool> ReleaseAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<GpuLockSelfTestResult> TestGpuClockLockAndResetAsync(
            uint minClockMHz = 210, uint maxClockMHz = 800, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The watcher does not run diagnostics.");
    }

    private sealed class ControlledPowerStatusProvider : IPowerStatusProvider
    {
        private readonly Channel<Func<PowerSourceMode>> _steps = Channel.CreateUnbounded<Func<PowerSourceMode>>();
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public PowerSourceMode? Fallback { get; init; }

        public void Enqueue(PowerSourceMode mode) => _steps.Writer.TryWrite(() => mode);
        public void EnqueueFailure(Exception exception) => _steps.Writer.TryWrite(() => throw exception);

        public async Task<PowerSourceMode> GetCurrentPowerSourceAsync(CancellationToken cancellationToken = default)
        {
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            if (_steps.Reader.TryRead(out var step)) return step();
            if (Fallback is { } mode) return mode;
            return (await _steps.Reader.ReadAsync(cancellationToken))();
        }

        public async Task WaitForReadAsync(int expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await _reads.Reader.ReadAsync(deadline.Token) < expected) { }
        }
    }

    private sealed class ManualTimestampProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
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
