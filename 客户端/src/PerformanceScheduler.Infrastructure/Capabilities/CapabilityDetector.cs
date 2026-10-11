using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Hardware.Gpu.Services;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Capabilities;

public sealed class CapabilityDetector : ICapabilityDetector, ICapabilityCacheInvalidator
{
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IGpuCapabilityProvider _gpuCapabilityProvider;
    private readonly IGpuTuningProviderRegistry _gpuTuningProviderRegistry;
    private readonly TimeSpan _cacheDuration;
    private readonly GpuControlReadinessEvaluator _gpuControlReadinessEvaluator = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly object _cacheStateLock = new();
    private CacheEntry? _cacheEntry;
    private long _cacheGeneration;

    public CapabilityDetector(
        IPowerPlanManager powerPlanManager,
        IGpuCapabilityProvider gpuCapabilityProvider,
        IGpuTuningProviderRegistry gpuTuningProviderRegistry,
        TimeSpan? cacheDuration = null)
    {
        _powerPlanManager = powerPlanManager;
        _gpuCapabilityProvider = gpuCapabilityProvider;
        _gpuTuningProviderRegistry = gpuTuningProviderRegistry;
        _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(30);
    }

    public async Task<CapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCachedSnapshot(out var cachedSnapshot))
        {
            return cachedSnapshot;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedSnapshot(out cachedSnapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return cachedSnapshot;
            }

            var detectionGeneration = GetCacheGeneration();
            var detectedSnapshot = await DetectCoreAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheStateLock)
            {
                // InvalidateCache may have been called while a provider was
                // running. Do not let that already-invalidated result become
                // the cache entry for the next scan.
                if (_cacheGeneration == detectionGeneration)
                {
                    _cacheEntry = new CacheEntry(detectedSnapshot, DateTimeOffset.UtcNow);
                }
            }

            return detectedSnapshot;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheStateLock)
        {
            _cacheGeneration++;
            _cacheEntry = null;
        }
    }

    private async Task<CapabilitySnapshot> DetectCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PowerPlanInfo> powerPlans;
        try
        {
            powerPlans = await _powerPlanManager.GetAvailablePlansAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A missing or unavailable power provider should degrade to an
            // unsupported capability, not abort detection for every feature.
            powerPlans = Array.Empty<PowerPlanInfo>();
        }

        var detectedGpu = await DetectGpuAsync(cancellationToken);
        var gpuProviderSupport = await DetectGpuProviderSupportAsync(detectedGpu, cancellationToken);
        var gpu = detectedGpu with
        {
            ProviderName = gpuProviderSupport.ProviderName,
            SupportsVendorExtensions = gpuProviderSupport.SupportsVendorExtensions,
            SupportsApplyPipeline = gpuProviderSupport.SupportsApplyPipeline,
            SupportedFeatures = gpuProviderSupport.SupportedFeatures
                .Distinct()
                .ToArray(),
            StatusKey = detectedGpu.StatusKey == CapabilityReasonCodes.GpuDetectionUnavailable
                ? detectedGpu.StatusKey
                : gpuProviderSupport.StatusKey
        };
        var unsupportedReasons = new List<string>();

        if (powerPlans.Count == 0)
        {
            unsupportedReasons.Add(CapabilityReasonCodes.NoPowerPlans);
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            unsupportedReasons.Add(CapabilityReasonCodes.EfficiencyModeUnavailable);
        }

        if (!OperatingSystem.IsWindows())
        {
            unsupportedReasons.Add(CapabilityReasonCodes.CpuAffinityUnavailable);
            unsupportedReasons.Add(CapabilityReasonCodes.MemoryIoPriorityUnavailable);
            unsupportedReasons.Add(CapabilityReasonCodes.JobObjectLimitsUnavailable);
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            unsupportedReasons.Add(CapabilityReasonCodes.CpuSetsUnavailable);
        }

        if (!gpu.HasDetectedAdapter)
        {
            unsupportedReasons.Add(
                gpu.StatusKey == CapabilityReasonCodes.GpuDetectionUnavailable
                    ? CapabilityReasonCodes.GpuDetectionUnavailable
                    : CapabilityReasonCodes.GpuAdapterUnavailable);
        }
        else if (gpu.StatusKey == CapabilityReasonCodes.GpuProviderUnavailable)
        {
            unsupportedReasons.Add(CapabilityReasonCodes.GpuProviderUnavailable);
        }
        else if (!gpu.SupportsVendorExtensions)
        {
            unsupportedReasons.Add(CapabilityReasonCodes.GpuVendorExtensionUnavailable);
        }
        else if (!gpu.SupportsApplyPipeline)
        {
            unsupportedReasons.Add(CapabilityReasonCodes.GpuProviderApplyUnavailable);
        }

        return new CapabilitySnapshot
        {
            SupportsPowerPlanSwitching = powerPlans.Count > 0,
            SupportsPriorityBoost = OperatingSystem.IsWindows(),
            SupportsRollback = true,
            SupportsMetricsCollection = true,
            SupportsEfficiencyModeHint = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299),
            SupportsCpuAffinity = OperatingSystem.IsWindows(),
            SupportsCpuSets = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393),
            SupportsMemoryIoPriority = OperatingSystem.IsWindows(),
            SupportsJobObjectLimits = OperatingSystem.IsWindows(),
            Gpu = gpu,
            GpuControl = _gpuControlReadinessEvaluator.Evaluate(gpu),
            AvailablePowerPlans = powerPlans,
            UnsupportedReasons = unsupportedReasons
        };
    }

    private async Task<GpuCapabilitySnapshot> DetectGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _gpuCapabilityProvider.DetectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot ?? new GpuCapabilitySnapshot
            {
                StatusKey = CapabilityReasonCodes.GpuDetectionUnavailable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GpuCapabilitySnapshot
            {
                StatusKey = CapabilityReasonCodes.GpuDetectionUnavailable
            };
        }
    }

    private async Task<GpuTuningProviderSupport> DetectGpuProviderSupportAsync(
        GpuCapabilitySnapshot detectedGpu,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = _gpuTuningProviderRegistry.Resolve(detectedGpu.Vendor);
            var support = await provider.GetSupportAsync(detectedGpu, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return support ?? new GpuTuningProviderSupport
            {
                ProviderName = "Unavailable",
                StatusKey = CapabilityReasonCodes.GpuProviderUnavailable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GpuTuningProviderSupport
            {
                ProviderName = "Unavailable",
                StatusKey = CapabilityReasonCodes.GpuProviderUnavailable
            };
        }
    }

    private bool TryGetCachedSnapshot(out CapabilitySnapshot snapshot)
    {
        lock (_cacheStateLock)
        {
            if (_cacheEntry is not null &&
                DateTimeOffset.UtcNow - _cacheEntry.CachedAt <= _cacheDuration)
            {
                snapshot = _cacheEntry.Snapshot;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private long GetCacheGeneration()
    {
        lock (_cacheStateLock)
        {
            return _cacheGeneration;
        }
    }

    private sealed record CacheEntry(
        CapabilitySnapshot Snapshot,
        DateTimeOffset CachedAt);
}
