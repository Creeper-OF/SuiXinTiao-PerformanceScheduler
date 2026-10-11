using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class DeviceStrategyRuntimeProfileResolver : IRuntimeProfileResolver
{
    private readonly IDeviceStrategyPackageStore _packageStore;
    private readonly IDeviceFingerprintProvider _deviceFingerprintProvider;
    private readonly DeviceStrategyRuntimeSelector _selector;
    private readonly TimeSpan _fingerprintCacheDuration;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _fingerprintGate = new(1, 1);
    private DeviceFingerprint? _cachedDevice;
    private DateTimeOffset _fingerprintCacheExpiresAt;

    public DeviceStrategyRuntimeProfileResolver(
        IDeviceStrategyPackageStore packageStore,
        IDeviceFingerprintProvider deviceFingerprintProvider,
        DeviceStrategyRuntimeSelector? selector = null,
        TimeSpan? fingerprintCacheDuration = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _packageStore = packageStore;
        _deviceFingerprintProvider = deviceFingerprintProvider;
        _selector = selector ?? new DeviceStrategyRuntimeSelector();
        _fingerprintCacheDuration = fingerprintCacheDuration ?? TimeSpan.FromMinutes(5);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DeviceStrategyRuntimeSelection> ResolveAsync(
        IReadOnlyList<PerformanceProfile> localProfiles,
        PowerSourceMode powerSourceMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localProfiles);

        var currentDevice = await GetCurrentDeviceAsync(cancellationToken);
        var packages = await _packageStore.LoadPackagesAsync(cancellationToken);
        return _selector.Select(currentDevice, powerSourceMode, packages, localProfiles);
    }

    public void InvalidateDeviceFingerprint()
    {
        _cachedDevice = null;
        _fingerprintCacheExpiresAt = default;
    }

    private async Task<DeviceFingerprint> GetCurrentDeviceAsync(CancellationToken cancellationToken)
    {
        await _fingerprintGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedDevice is not null && _utcNow() < _fingerprintCacheExpiresAt)
            {
                return _cachedDevice;
            }

            _cachedDevice = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken);
            _fingerprintCacheExpiresAt = _utcNow() + _fingerprintCacheDuration;
            return _cachedDevice;
        }
        finally
        {
            _fingerprintGate.Release();
        }
    }
}
