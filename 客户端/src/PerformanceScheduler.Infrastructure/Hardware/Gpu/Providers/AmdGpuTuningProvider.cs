using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.AmdAdlx;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public sealed class AmdGpuTuningProvider : IGpuTuningProvider
{
    private readonly IAmdGpuSource _amdSource;
    private readonly IGpuClockRecoveryStore? _recoveryStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _recoveryPending;

    public AmdGpuTuningProvider(IAmdGpuSource? amdSource = null, IGpuClockRecoveryStore? recoveryStore = null)
    {
        _recoveryStore = recoveryStore;
        _amdSource = amdSource ?? (OperatingSystem.IsWindows()
            ? new AmdGpuSource()
            : new NullAmdGpuSource());
    }

    public GpuVendor Vendor => GpuVendor.Amd;

    public string ProviderName => "AMD ADLX";

    public async Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var availability = await _amdSource.GetAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            return new GpuTuningProviderSupport
            {
                ProviderName = ProviderName,
                SupportsVendorExtensions = true,
                SupportsApplyPipeline = false,
                SupportedFeatures = Array.Empty<GpuTuningFeature>(),
                StatusKey = "Capability.GpuUnavailable"
            };
        }

        if (availability.SupportsClockLocking)
        {
            return new GpuTuningProviderSupport
            {
                ProviderName = ProviderName,
                SupportsVendorExtensions = true,
                SupportsApplyPipeline = true,
                SupportedFeatures =
                [
                    GpuTuningFeature.Telemetry,
                    GpuTuningFeature.ResetToDefault,
                    GpuTuningFeature.CoreClockLimit
                ],
                StatusKey = "Capability.GpuReversibleLimitReady"
            };
        }

        return new GpuTuningProviderSupport
        {
            ProviderName = ProviderName,
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = false,
            SupportedFeatures = [GpuTuningFeature.Telemetry],
            StatusKey = "Capability.GpuReadOnlyTelemetry"
        };
    }

    public async Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (minGpuClockMHz == 0 || maxGpuClockMHz < minGpuClockMHz)
        {
            return false;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_recoveryStore is not null)
            {
                await _recoveryStore.IsPendingAsync(cancellationToken);
            }

            var availability = await _amdSource.GetAvailabilityAsync(cancellationToken);
            if (!availability.IsAvailable || !availability.SupportsClockLocking)
            {
                return false;
            }

            if (_recoveryStore is not null)
            {
                await _recoveryStore.MarkPendingAsync(cancellationToken);
            }

            _recoveryPending = true;
            return await _amdSource.TrySetLockedClocksAsync(minGpuClockMHz, maxGpuClockMHz, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var pending = _recoveryPending;
            if (_recoveryStore is not null)
            {
                pending |= await _recoveryStore.IsPendingAsync(cancellationToken);
            }

            if (!pending)
            {
                return true;
            }

            if (!await _amdSource.TryResetLockedClocksAsync(cancellationToken))
            {
                return false;
            }

            if (_recoveryStore is not null)
            {
                await _recoveryStore.ClearAsync(cancellationToken);
            }

            _recoveryPending = false;
            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
