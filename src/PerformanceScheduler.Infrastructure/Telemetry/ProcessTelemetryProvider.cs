using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Infrastructure.Telemetry;

public sealed class ProcessTelemetryProvider : ITelemetryProvider
{
    public const string ProviderName = "windows.process";

    private readonly IMetricsCollector _metricsCollector;

    public ProcessTelemetryProvider(IMetricsCollector metricsCollector)
    {
        _metricsCollector = metricsCollector;
    }

    public string ProviderId => ProviderName;

    public Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TelemetryProviderCapabilities
        {
            ProviderId = ProviderId,
            IsSupported = true,
            SupportedMetrics =
            [
                TelemetryMetricKind.ProcessCpuUsagePercent,
                TelemetryMetricKind.WorkingSetMegabytes,
                TelemetryMetricKind.PrivateMemoryMegabytes,
                TelemetryMetricKind.ThreadCount,
                TelemetryMetricKind.HandleCount
            ],
            MinimumSamplingInterval = TimeSpan.FromMilliseconds(500),
            EstimatedOverhead = TelemetryOverheadClass.Low
        });
    }

    public async Task<TelemetryProviderSample> CollectAsync(
        FocusedAppContext target,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var snapshot = await _metricsCollector.CollectAsync(target, cancellationToken);
        stopwatch.Stop();
        if (!snapshot.IsAvailable)
        {
            return new TelemetryProviderSample
            {
                ProviderId = ProviderId,
                CollectedAt = snapshot.CollectedAt,
                Status = TelemetryCollectionStatus.Unavailable,
                CollectionDuration = stopwatch.Elapsed,
                ReasonCode = TelemetryReasonCodes.ProcessUnavailable
            };
        }

        var metrics = new List<TelemetryMetricValue>(5)
        {
            Metric(TelemetryMetricKind.WorkingSetMegabytes, snapshot.WorkingSetMb, "MiB"),
            Metric(TelemetryMetricKind.PrivateMemoryMegabytes, snapshot.PrivateMemoryMb, "MiB"),
            Metric(TelemetryMetricKind.ThreadCount, snapshot.ThreadCount, "count"),
            Metric(TelemetryMetricKind.HandleCount, snapshot.HandleCount, "count")
        };
        if (snapshot.CpuUsagePercent is { } cpuUsage)
        {
            metrics.Insert(0, Metric(TelemetryMetricKind.ProcessCpuUsagePercent, cpuUsage, "%"));
        }

        return new TelemetryProviderSample
        {
            ProviderId = ProviderId,
            CollectedAt = snapshot.CollectedAt,
            Status = snapshot.CpuUsagePercent is null
                ? TelemetryCollectionStatus.Warmup
                : TelemetryCollectionStatus.Collected,
            Metrics = metrics,
            CollectionDuration = stopwatch.Elapsed,
            ReasonCode = snapshot.CpuUsagePercent is null ? TelemetryReasonCodes.CpuWarmup : string.Empty
        };
    }

    private static TelemetryMetricValue Metric(TelemetryMetricKind kind, double value, string unit) =>
        new()
        {
            Kind = kind,
            Value = value,
            Unit = unit
        };
}
