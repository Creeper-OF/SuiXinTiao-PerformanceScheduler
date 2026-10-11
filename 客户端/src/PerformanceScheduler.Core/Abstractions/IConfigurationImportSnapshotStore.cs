using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

/// <summary>
/// Provides a raw snapshot boundary for multi-store configuration imports.
/// Implementations must restore their snapshot without depending on the caller's
/// cancellation token so a partially applied import can be made safe again.
/// </summary>
public interface IConfigurationImportSnapshotStore
{
    Task<ConfigurationStoreSnapshot> CaptureImportSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task RestoreImportSnapshotAsync(
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
