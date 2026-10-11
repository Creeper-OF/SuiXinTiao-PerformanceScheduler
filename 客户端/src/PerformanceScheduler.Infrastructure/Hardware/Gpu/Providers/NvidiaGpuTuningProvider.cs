using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

public sealed class NvidiaGpuTuningProvider : IGpuTuningProvider
{
    private readonly INvidiaNvmlSource _nvmlSource;
    private readonly IGpuClockRecoveryStore? _recoveryStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _recoveryPending;

    public NvidiaGpuTuningProvider(INvidiaNvmlSource? nvmlSource = null, IGpuClockRecoveryStore? recoveryStore = null)
    {
        _recoveryStore = recoveryStore;
        _nvmlSource = nvmlSource ?? (OperatingSystem.IsWindows()
            ? new NvidiaNvmlSource()
            : new NullNvidiaNvmlSource());
    }

    public GpuVendor Vendor => GpuVendor.Nvidia;

    public string ProviderName => "NVIDIA NVML";

    public async Task<GpuTuningProviderSupport> GetSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var availability = await _nvmlSource.GetAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            return new GpuTuningProviderSupport
            {
                ProviderName = ProviderName,
                SupportsVendorExtensions = false,
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
                // Validate any existing recovery record before considering a new write.
                await _recoveryStore.IsPendingAsync(cancellationToken);
            }

            var availability = await _nvmlSource.GetAvailabilityAsync(cancellationToken);
            if (!availability.IsAvailable || !availability.SupportsClockLocking)
            {
                return false;
            }

            if (_recoveryStore is not null)
            {
                await _recoveryStore.MarkPendingAsync(cancellationToken);
            }

            // A failed/cancelled batch may already have changed some devices.
            // Keep recovery pending until every reset and marker cleanup succeeds.
            _recoveryPending = true;
            return await _nvmlSource.TrySetLockedClocksAsync(minGpuClockMHz, maxGpuClockMHz, cancellationToken);
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

            if (!await _nvmlSource.TryResetLockedClocksAsync(cancellationToken))
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

    private sealed class NullNvidiaNvmlSource : INvidiaNvmlSource
    {
        public Task<NvidiaNvmlAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NvidiaNvmlAvailability { FailureReason = "Unsupported OS platform." });

        public Task<NvidiaNvmlSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NvidiaNvmlSnapshot());

        public Task<bool> TrySetLockedClocksAsync(
            uint minGpuClockMHz,
            uint maxGpuClockMHz,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
