using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Capabilities;

public sealed class CapabilityDetector : ICapabilityDetector
{
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IGpuCapabilityProvider _gpuCapabilityProvider;
    private readonly IGpuTuningProviderRegistry _gpuTuningProviderRegistry;
    private readonly TimeSpan _cacheDuration;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private CapabilitySnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

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
        if (TryGetCachedSnapshot(out var cachedSnapshot))
        {
            return cachedSnapshot;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedSnapshot(out cachedSnapshot))
            {
                return cachedSnapshot;
            }

            _cachedSnapshot = await DetectCoreAsync(cancellationToken);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedSnapshot;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<CapabilitySnapshot> DetectCoreAsync(CancellationToken cancellationToken)
    {
        var powerPlans = await _powerPlanManager.GetAvailablePlansAsync(cancellationToken);
        var detectedGpu = await _gpuCapabilityProvider.DetectAsync(cancellationToken);
        var gpuProvider = _gpuTuningProviderRegistry.Resolve(detectedGpu.Vendor);
        var gpuProviderSupport = await gpuProvider.GetSupportAsync(detectedGpu, cancellationToken);
        var gpu = detectedGpu with
        {
            ProviderName = gpuProviderSupport.ProviderName,
            SupportsVendorExtensions = gpuProviderSupport.SupportsVendorExtensions,
            SupportsApplyPipeline = gpuProviderSupport.SupportsApplyPipeline,
            SupportsClockLimit = gpuProviderSupport.SupportsClockLimit,
            SupportsVoltageControl = gpuProviderSupport.SupportsVoltageControl,
            StatusKey = gpuProviderSupport.StatusKey
        };
        var unsupportedReasons = new List<string>();

        if (powerPlans.Count == 0)
        {
            unsupportedReasons.Add("No switchable power plans were detected.");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            unsupportedReasons.Add("Efficiency mode hint is not exposed on this Windows build.");
        }

        if (!gpu.HasDetectedAdapter)
        {
            unsupportedReasons.Add("No configurable GPU adapter was detected through public Windows interfaces.");
        }
        else if (!gpu.SupportsVendorExtensions)
        {
            unsupportedReasons.Add("The detected GPU vendor does not expose a recognized vendor extension slot yet.");
        }
        else if (!gpu.SupportsApplyPipeline)
        {
            unsupportedReasons.Add($"GPU provider {gpu.ProviderName} is registered, but live tuning is not wired in yet.");
        }

        return new CapabilitySnapshot
        {
            SupportsPowerPlanSwitching = powerPlans.Count > 0,
            SupportsPriorityBoost = OperatingSystem.IsWindows(),
            SupportsRollback = true,
            SupportsMetricsCollection = true,
            SupportsEfficiencyModeHint = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            Gpu = gpu,
            AvailablePowerPlans = powerPlans,
            UnsupportedReasons = unsupportedReasons
        };
    }

    private bool TryGetCachedSnapshot(out CapabilitySnapshot snapshot)
    {
        if (_cachedSnapshot is not null && DateTimeOffset.UtcNow - _cachedAt <= _cacheDuration)
        {
            snapshot = _cachedSnapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }
}
