using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Power;

public sealed class Win32PowerChangeWatcher : IAsyncDisposable
{
    private readonly IPowerStatusProvider _powerStatusProvider;
    private readonly IPowerEmergencyGate _powerEmergencyGate;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _retryInterval;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();
    private Task? _watchTask;
    private Task? _disposeTask;
    private PowerSourceMode _lastMode = PowerSourceMode.Any;
    private PowerSourceMode? _failedMode;
    private long _lastFailureTimestamp;

    public Win32PowerChangeWatcher(
        IPowerStatusProvider powerStatusProvider,
        IPowerEmergencyGate powerEmergencyGate,
        IAppLogger logger,
        TimeSpan? pollingInterval = null,
        TimeSpan? retryInterval = null,
        TimeProvider? timeProvider = null)
    {
        _powerStatusProvider = powerStatusProvider;
        _powerEmergencyGate = powerEmergencyGate;
        _logger = logger;
        _pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(150);
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_pollingInterval < TimeSpan.FromMilliseconds(1) ||
            _pollingInterval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pollingInterval));
        }
        if (_retryInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _watchTask ??= Task.Run(WatchLoopAsync);
        }
    }

    private async Task WatchLoopAsync()
    {
        using var timer = new PeriodicTimer(_pollingInterval);
        try
        {
            // Reconcile the first observation as well as later ticks. Starting
            // on battery must not wait for a future AC->DC edge.
            do
            {
                await ObservePowerAsync(_cts.Token);
            }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown; independent provider cancellations are retried.
        }
    }

    private async Task ObservePowerAsync(CancellationToken cancellationToken)
    {
        var currentMode = PowerSourceMode.Any;
        try
        {
            currentMode = await _powerStatusProvider.GetCurrentPowerSourceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("[Win32PowerChangeWatcher] Power status read failed; monitoring will continue.", ex);
            // Unknown power retains any clamp and prevents new live diagnostics.
        }

        var previousMode = _lastMode;
        _lastMode = currentMode;
        if (_failedMode == currentMode && previousMode == currentMode &&
            _timeProvider.GetElapsedTime(_lastFailureTimestamp) < _retryInterval)
        {
            return;
        }

        try
        {
            var succeeded = await _powerEmergencyGate.HandlePowerTransitionAsync(
                previousMode, currentMode, cancellationToken);
            if (succeeded)
            {
                _failedMode = null;
                return;
            }
            _logger.Warn("[Win32PowerChangeWatcher] GPU power reconciliation was incomplete; it will be retried.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("[Win32PowerChangeWatcher] GPU power reconciliation failed; it will be retried.", ex);
        }

        _failedMode = currentMode;
        _lastFailureTimestamp = _timeProvider.GetTimestamp();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            // Cancel outside this lock: cancellation callbacks may call back
            // into lifecycle methods on another thread.
            _disposeTask ??= Task.Run(StopAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StopAsync()
    {
        _cts.Cancel();
        try
        {
            if (_watchTask is not null)
            {
                await _watchTask;
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
