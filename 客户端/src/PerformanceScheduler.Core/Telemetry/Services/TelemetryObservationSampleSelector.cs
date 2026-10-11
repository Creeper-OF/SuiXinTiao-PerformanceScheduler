using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

internal static class TelemetryObservationSampleSelector
{
    public static IEnumerable<TelemetryProviderSample> CollectedSamples(
        TelemetryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return observation.ProviderSamples
            .Where(sample => sample.Status == TelemetryCollectionStatus.Collected);
    }
}
