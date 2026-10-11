using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

namespace PerformanceScheduler.Infrastructure.Telemetry.NvidiaNvml;

public sealed class NvidiaNvmlTelemetryProvider : ITelemetryProvider
{
    public const string Id = "nvidia.nvml";

    private readonly INvidiaNvmlSource _source;

    public NvidiaNvmlTelemetryProvider(INvidiaNvmlSource source)
    {
        _source = source;
    }

    public string ProviderId => Id;

    public async Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UnsupportedCapabilities();
        }

        try
        {
            var availability = await _source.GetAvailabilityAsync(cancellationToken);
            return new TelemetryProviderCapabilities
            {
                ProviderId = ProviderId,
                IsSupported = availability.IsAvailable,
                SupportedMetrics = availability.IsAvailable
                    ?
                    [
                        TelemetryMetricKind.NvidiaGpuUsagePercent,
                        TelemetryMetricKind.NvidiaGpuTemperatureCelsius,
                        TelemetryMetricKind.NvidiaGpuPowerWatts,
                        TelemetryMetricKind.NvidiaGpuGraphicsClockMegahertz
                    ]
                    : Array.Empty<TelemetryMetricKind>(),
                MinimumSamplingInterval = TimeSpan.FromSeconds(2),
                EstimatedOverhead = TelemetryOverheadClass.Medium,
                UnavailableReasonCode = availability.IsAvailable
                    ? string.Empty
                    : TelemetryReasonCodes.NvidiaNvmlUnavailable
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return UnsupportedCapabilities();
        }
    }

    public async Task<TelemetryProviderSample> CollectAsync(
        FocusedAppContext target,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var snapshot = await _source.CollectAsync(cancellationToken);
            if (!snapshot.HasAnyData)
            {
                return Status(
                    TelemetryCollectionStatus.Unavailable,
                    TelemetryReasonCodes.NvidiaNvmlNoSamples,
                    Stopwatch.GetElapsedTime(startedAt));
            }

            var metrics = new List<TelemetryMetricValue>(4);
            AddMetric(metrics, TelemetryMetricKind.NvidiaGpuUsagePercent, snapshot.UsagePercent, "%");
            AddMetric(
                metrics,
                TelemetryMetricKind.NvidiaGpuTemperatureCelsius,
                snapshot.TemperatureCelsius,
                "\u00B0C");
            AddMetric(metrics, TelemetryMetricKind.NvidiaGpuPowerWatts, snapshot.PowerWatts, "W");
            AddMetric(
                metrics,
                TelemetryMetricKind.NvidiaGpuGraphicsClockMegahertz,
                snapshot.GraphicsClockMegahertz,
                "MHz");
            return new TelemetryProviderSample
            {
                ProviderId = ProviderId,
                Status = TelemetryCollectionStatus.Collected,
                Metrics = metrics,
                CollectionDuration = Stopwatch.GetElapsedTime(startedAt)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TelemetryProviderSample
            {
                ProviderId = ProviderId,
                Status = TelemetryCollectionStatus.Failed,
                ReasonCode = TelemetryReasonCodes.NvidiaNvmlQueryFailed,
                TechnicalMessage = Truncate(exception.Message),
                CollectionDuration = Stopwatch.GetElapsedTime(startedAt)
            };
        }
    }

    private static TelemetryProviderCapabilities UnsupportedCapabilities() =>
        new()
        {
            ProviderId = Id,
            IsSupported = false,
            MinimumSamplingInterval = TimeSpan.FromSeconds(2),
            EstimatedOverhead = TelemetryOverheadClass.Medium,
            UnavailableReasonCode = TelemetryReasonCodes.NvidiaNvmlUnavailable
        };

    private static TelemetryProviderSample Status(
        TelemetryCollectionStatus status,
        string reasonCode,
        TimeSpan duration) =>
        new()
        {
            ProviderId = Id,
            Status = status,
            ReasonCode = reasonCode,
            CollectionDuration = duration
        };

    private static void AddMetric(
        ICollection<TelemetryMetricValue> metrics,
        TelemetryMetricKind kind,
        double? value,
        string unit)
    {
        if (value is { } measured && double.IsFinite(measured) && measured >= 0)
        {
            metrics.Add(new TelemetryMetricValue { Kind = kind, Value = measured, Unit = unit });
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, 1000)];
    }
}
