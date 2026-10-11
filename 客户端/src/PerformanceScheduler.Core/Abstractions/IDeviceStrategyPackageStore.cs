using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IDeviceStrategyPackageStore : IConfigurationImportSnapshotStore
{
    Task<IReadOnlyList<DeviceStrategyPackage>> LoadPackagesAsync(CancellationToken cancellationToken = default);

    Task SavePackageAsync(DeviceStrategyPackage package, CancellationToken cancellationToken = default);
}
