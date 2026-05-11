using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IDeviceStrategyPackageStore
{
    Task<IReadOnlyList<DeviceStrategyPackage>> LoadPackagesAsync(CancellationToken cancellationToken = default);

    Task SavePackageAsync(DeviceStrategyPackage package, CancellationToken cancellationToken = default);
}
