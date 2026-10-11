using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IDeviceFingerprintProvider
{
    Task<DeviceFingerprint> GetCurrentAsync(CancellationToken cancellationToken = default);
}
