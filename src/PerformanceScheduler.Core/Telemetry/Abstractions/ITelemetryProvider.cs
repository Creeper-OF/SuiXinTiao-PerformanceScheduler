using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Abstractions;

public interface ITelemetryProvider
{
    string ProviderId { get; }

    Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<TelemetryProviderSample> CollectAsync(
        FocusedAppContext target,
        CancellationToken cancellationToken = default);
}
