using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;
using PerformanceScheduler.Infrastructure.Telemetry.NvidiaNvml;

namespace PerformanceScheduler.Tests;

public sealed class NvidiaNvmlTelemetryProviderTests
{
    [Fact]
    public async Task RealNvmlSource_DegradesOrCollectsWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new NvidiaNvmlSource();
        var availability = await source.GetAvailabilityAsync();
        if (!availability.IsAvailable)
        {
            Assert.NotEmpty(availability.FailureReason);
            return;
        }

        var snapshot = await source.CollectAsync();

        Assert.True(snapshot.HasAnyData);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenNvmlIsAvailable_ExposesReadOnlyMetrics()
    {
        var provider = new NvidiaNvmlTelemetryProvider(new FakeSource
        {
            Availability = new NvidiaNvmlAvailability
            {
                IsAvailable = true,
                DeviceCount = 1
            }
        });

        var capabilities = await provider.GetCapabilitiesAsync();

        Assert.True(capabilities.IsSupported);
        Assert.Equal(
            [
                TelemetryMetricKind.NvidiaGpuUsagePercent,
                TelemetryMetricKind.NvidiaGpuTemperatureCelsius,
                TelemetryMetricKind.NvidiaGpuPowerWatts,
                TelemetryMetricKind.NvidiaGpuGraphicsClockMegahertz
            ],
            capabilities.SupportedMetrics);
        Assert.Equal(TimeSpan.FromSeconds(2), capabilities.MinimumSamplingInterval);
    }

    [Fact]
    public async Task CollectAsync_ReportsNvmlMetrics()
    {
        var provider = new NvidiaNvmlTelemetryProvider(new FakeSource
        {
            Snapshot = new NvidiaNvmlSnapshot
            {
                UsagePercent = 64,
                TemperatureCelsius = 68,
                PowerWatts = 82.5,
                GraphicsClockMegahertz = 1890
            }
        });

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Collection(
            sample.Metrics,
            metric => AssertMetric(metric, TelemetryMetricKind.NvidiaGpuUsagePercent, 64, "%"),
            metric => AssertMetric(metric, TelemetryMetricKind.NvidiaGpuTemperatureCelsius, 68, "\u00B0C"),
            metric => AssertMetric(metric, TelemetryMetricKind.NvidiaGpuPowerWatts, 82.5, "W"),
            metric => AssertMetric(metric, TelemetryMetricKind.NvidiaGpuGraphicsClockMegahertz, 1890, "MHz"));
    }

    [Fact]
    public async Task CollectAsync_WithoutNvmlSamples_ReturnsUnavailable()
    {
        var provider = new NvidiaNvmlTelemetryProvider(new FakeSource());

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Unavailable, sample.Status);
        Assert.Equal(TelemetryReasonCodes.NvidiaNvmlNoSamples, sample.ReasonCode);
    }

    [Fact]
    public async Task CollectAsync_WhenNvmlFails_ReturnsStableFailureReason()
    {
        var provider = new NvidiaNvmlTelemetryProvider(new FakeSource
        {
            CollectionException = new InvalidOperationException("NVML query failed.")
        });

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Failed, sample.Status);
        Assert.Equal(TelemetryReasonCodes.NvidiaNvmlQueryFailed, sample.ReasonCode);
        Assert.Contains("NVML query failed", sample.TechnicalMessage);
    }

    private static FocusedAppContext CreateTarget() =>
        new()
        {
            ProcessId = 42,
            ProcessName = "game",
            WindowTitle = "Game",
            WindowHandle = (nint)1
        };

    private static void AssertMetric(
        TelemetryMetricValue metric,
        TelemetryMetricKind kind,
        double value,
        string unit)
    {
        Assert.Equal(kind, metric.Kind);
        Assert.Equal(value, metric.Value, precision: 6);
        Assert.Equal(unit, metric.Unit);
    }

    private sealed class FakeSource : INvidiaNvmlSource
    {
        public NvidiaNvmlAvailability Availability { get; init; } = new();

        public NvidiaNvmlSnapshot Snapshot { get; init; } = new();

        public Exception? CollectionException { get; init; }

        public Task<NvidiaNvmlAvailability> GetAvailabilityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<NvidiaNvmlSnapshot> CollectAsync(
            CancellationToken cancellationToken = default) =>
            CollectionException is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<NvidiaNvmlSnapshot>(CollectionException);

        public Task<bool> TrySetLockedClocksAsync(
            uint minGpuClockMHz,
            uint maxGpuClockMHz,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TryResetLockedClocksAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
