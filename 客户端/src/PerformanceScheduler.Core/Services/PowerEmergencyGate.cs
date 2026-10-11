using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class PowerEmergencyGate : IPowerEmergencyGate
{
    private readonly IGpuTuningProvider _gpuProvider;
    private readonly IAppLogger _logger;
    private readonly uint _emergencyMinClockMHz;
    private readonly uint _emergencyMaxClockMHz;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _isThrottled;
    private bool _recoveryRequired;
    private PowerSourceMode _observedPowerMode = PowerSourceMode.Any;

    public PowerEmergencyGate(
        IGpuTuningProvider gpuProvider,
        IAppLogger logger,
        uint emergencyMinClockMHz = 210,
        uint emergencyMaxClockMHz = 800)
    {
        _gpuProvider = gpuProvider;
        _logger = logger;
        _emergencyMinClockMHz = emergencyMinClockMHz;
        _emergencyMaxClockMHz = emergencyMaxClockMHz;
    }

    public bool IsThrottled
    {
        get
        {
            lock (_stateLock)
            {
                return _isThrottled;
            }
        }
    }

    public async Task<bool> HandlePowerTransitionAsync(
        PowerSourceMode previousMode,
        PowerSourceMode currentMode,
        CancellationToken cancellationToken = default)
    {
        // Record observations before waiting for an in-flight diagnostic. A
        // diagnostic still checking capabilities must not start writing on DC.
        lock (_stateLock)
        {
            _observedPowerMode = currentMode;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (currentMode == PowerSourceMode.Battery)
            {
                if (IsThrottled)
                {
                    return true;
                }

                _logger.Warn(
                    $"[PowerEmergencyGate] Battery transition detected. Requesting GPU clock range {_emergencyMinClockMHz}-{_emergencyMaxClockMHz} MHz.");
                _recoveryRequired = true;
                var success = await _gpuProvider.TrySetLockedClocksAsync(
                    _emergencyMinClockMHz,
                    _emergencyMaxClockMHz,
                    cancellationToken);

                if (success)
                {
                    lock (_stateLock)
                    {
                        _isThrottled = true;
                    }
                    _logger.Info($"[PowerEmergencyGate] GPU emergency clock clamp applied ({_emergencyMaxClockMHz} MHz).");
                }
                else
                {
                    _logger.Error("[PowerEmergencyGate] Failed to apply GPU emergency clock clamp; recovery may still be required.");
                }

                return success;
            }

            if (currentMode == PowerSourceMode.Ac)
            {
                return await ReleaseCoreAsync(cancellationToken);
            }

            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await ReleaseCoreAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            // The provider also knows about persisted recovery from past sessions.
            _recoveryRequired = true;
            return await ReleaseCoreAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GpuLockSelfTestResult> TestGpuClockLockAndResetAsync(
        uint minClockMHz = 210,
        uint maxClockMHz = 800,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        var supported = false;
        var lockAttempted = false;
        try
        {
            var support = await _gpuProvider.GetSupportAsync(new GpuCapabilitySnapshot
            {
                HasDetectedAdapter = true,
                Vendor = _gpuProvider.Vendor,
                AdapterName = "GPU Probe"
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            supported = support.SupportsClockLimit;
            if (!supported)
            {
                return Result(true, "GPU clock locking is unavailable; diagnostic skipped.", skipped: true);
            }

            lock (_stateLock)
            {
                if (_observedPowerMode != PowerSourceMode.Ac || _recoveryRequired)
                {
                    return Result(true,
                        "GPU diagnostic skipped: confirmed AC power and completed recovery are required.",
                        skipped: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (minClockMHz == 0 || maxClockMHz < minClockMHz)
            {
                return Result(false, "Invalid GPU clock range; no hardware write was attempted.");
            }

            lockAttempted = true;
            _recoveryRequired = true;
            var lockOk = await _gpuProvider.TrySetLockedClocksAsync(minClockMHz, maxClockMHz, cancellationToken);
            // Even false can mean a partial multi-device write. Cleanup owns the
            // same operation lock and must finish before queued power actions.
            var resetOk = await ReleaseCoreAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (!lockOk)
            {
                return Result(false, resetOk
                    ? "Failed to apply GPU locked clocks; recovery completed."
                    : "Failed to apply GPU locked clocks; recovery is still required.");
            }

            return resetOk
                ? Result(true, $"GPU lock ({minClockMHz}-{maxClockMHz}MHz) and reset commands completed successfully.")
                : Result(false, "GPU lock completed, but recovery is still required.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lockAttempted && _recoveryRequired)
            {
                await TryRecoverDiagnosticAsync();
            }
            throw;
        }
        catch (Exception ex)
        {
            if (lockAttempted && _recoveryRequired)
            {
                await TryRecoverDiagnosticAsync();
            }
            return Result(false, $"GPU self-test failed with exception: {ex.Message}");
        }
        finally
        {
            sw.Stop();
            _operationGate.Release();
        }

        GpuLockSelfTestResult Result(bool success, string message, bool skipped = false) =>
            new(success, _gpuProvider.Vendor.ToString(), _gpuProvider.ProviderName, supported,
                minClockMHz, maxClockMHz, sw.ElapsedMilliseconds, message) { Skipped = skipped };
    }

    private async Task TryRecoverDiagnosticAsync()
    {
        try
        {
            if (!await ReleaseCoreAsync(CancellationToken.None))
            {
                _logger.Error("GPU self-test recovery was incomplete; pending recovery state was preserved.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("GPU self-test recovery could not complete; pending recovery state was preserved.", ex);
        }
    }

    private async Task<bool> ReleaseCoreAsync(CancellationToken cancellationToken)
    {
        if (!_recoveryRequired)
        {
            return true;
        }

        _logger.Info("[PowerEmergencyGate] Releasing GPU emergency clock clamp.");
        // A failed reset batch may have released only some devices. Until a
        // fresh successful clamp, the all-device throttled state is unconfirmed.
        lock (_stateLock)
        {
            _isThrottled = false;
        }
        var success = await _gpuProvider.TryResetLockedClocksAsync(cancellationToken);
        lock (_stateLock)
        {
            if (success)
            {
                _isThrottled = false;
                _recoveryRequired = false;
            }
        }

        return success;
    }
}
