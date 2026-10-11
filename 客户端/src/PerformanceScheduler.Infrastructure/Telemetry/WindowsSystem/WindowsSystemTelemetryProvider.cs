using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

public sealed class WindowsSystemTelemetryProvider : ITelemetryProvider
{
    public const string Id = "windows.system-hardware";
    private const double BytesPerMebibyte = 1024d * 1024d;

    private readonly IWindowsSystemPerformanceCounterSource _counterSource;

    public WindowsSystemTelemetryProvider(IWindowsSystemPerformanceCounterSource counterSource)
    {
        _counterSource = counterSource;
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
            var availability = await _counterSource.GetAvailabilityAsync(cancellationToken);
            var supportedMetrics = new List<TelemetryMetricKind>();
            if (availability.SupportsCpuFrequency)
            {
                supportedMetrics.Add(TelemetryMetricKind.SystemCpuFrequencyMegahertz);
            }

            if (availability.SupportsCpuUtilization)
            {
                supportedMetrics.Add(TelemetryMetricKind.SystemCpuUsagePercent);
            }

            if (availability.SupportsThermalZoneTemperature)
            {
                supportedMetrics.Add(TelemetryMetricKind.ThermalZoneTemperatureCelsius);
            }

            if (availability.SupportsGpuUtilization)
            {
                supportedMetrics.Add(TelemetryMetricKind.SystemGpuUsagePercent);
            }

            if (availability.SupportsGpuMemory)
            {
                supportedMetrics.Add(TelemetryMetricKind.SystemGpuDedicatedMemoryMegabytes);
                supportedMetrics.Add(TelemetryMetricKind.SystemGpuSharedMemoryMegabytes);
            }

            return new TelemetryProviderCapabilities
            {
                ProviderId = ProviderId,
                IsSupported = availability.IsSupported,
                SupportedMetrics = supportedMetrics,
                MinimumSamplingInterval = TimeSpan.FromSeconds(2),
                EstimatedOverhead = TelemetryOverheadClass.Medium,
                UnavailableReasonCode = availability.IsSupported
                    ? string.Empty
                    : TelemetryReasonCodes.SystemPerformanceCountersUnavailable
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
            var snapshot = await _counterSource.CollectAsync(cancellationToken);
            if (!snapshot.HasAnyData)
            {
                return Status(
                    TelemetryCollectionStatus.Unavailable,
                    TelemetryReasonCodes.SystemPerformanceCounterNoSamples,
                    Stopwatch.GetElapsedTime(startedAt));
            }

            var metrics = new List<TelemetryMetricValue>(6);
            AddMetric(
                metrics,
                TelemetryMetricKind.SystemCpuFrequencyMegahertz,
                snapshot.CpuFrequencyMegahertz,
                "MHz");
            AddMetric(
                metrics,
                TelemetryMetricKind.SystemCpuUsagePercent,
                snapshot.CpuUsagePercent,
                "%");
            AddMetric(
                metrics,
                TelemetryMetricKind.ThermalZoneTemperatureCelsius,
                snapshot.ThermalZoneTemperatureCelsius,
                "\u00B0C");
            AddMetric(
                metrics,
                TelemetryMetricKind.SystemGpuUsagePercent,
                snapshot.GpuUsagePercent,
                "%");
            AddMetric(
                metrics,
                TelemetryMetricKind.SystemGpuDedicatedMemoryMegabytes,
                snapshot.GpuDedicatedMemoryBytes / BytesPerMebibyte,
                "MiB");
            AddMetric(
                metrics,
                TelemetryMetricKind.SystemGpuSharedMemoryMegabytes,
                snapshot.GpuSharedMemoryBytes / BytesPerMebibyte,
                "MiB");
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
                ReasonCode = TelemetryReasonCodes.SystemPerformanceCounterQueryFailed,
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
            UnavailableReasonCode = TelemetryReasonCodes.SystemPerformanceCountersUnavailable
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
