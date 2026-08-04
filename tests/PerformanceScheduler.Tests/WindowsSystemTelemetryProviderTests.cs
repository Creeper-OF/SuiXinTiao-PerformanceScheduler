using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

namespace PerformanceScheduler.Tests;

public sealed class WindowsSystemTelemetryProviderTests
{
    [Fact]
    public async Task GetCapabilitiesAsync_ReflectsAvailableCounterGroups()
    {
        var source = new FakeSource
        {
            Availability = new WindowsSystemPerformanceCounterAvailability
            {
                SupportsCpuFrequency = true,
                SupportsThermalZoneTemperature = false
            }
        };
        var provider = new WindowsSystemTelemetryProvider(source);

        var capabilities = await provider.GetCapabilitiesAsync();

        Assert.True(capabilities.IsSupported);
        Assert.Equal(
            [TelemetryMetricKind.SystemCpuFrequencyMegahertz],
            capabilities.SupportedMetrics);
        Assert.Equal(TimeSpan.FromSeconds(2), capabilities.MinimumSamplingInterval);
    }

    [Fact]
    public async Task CollectAsync_ReportsFrequencyAndThermalZoneTemperature()
    {
        var provider = new WindowsSystemTelemetryProvider(new FakeSource
        {
            Snapshot = new WindowsSystemPerformanceSnapshot
            {
                CpuFrequencyMegahertz = 3976,
                ThermalZoneTemperatureCelsius = 55.25
            }
        });

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Collection(
            sample.Metrics,
            metric => AssertMetric(metric, TelemetryMetricKind.SystemCpuFrequencyMegahertz, 3976, "MHz"),
            metric => AssertMetric(metric, TelemetryMetricKind.ThermalZoneTemperatureCelsius, 55.25, "°C"));
    }

    [Fact]
    public async Task CollectAsync_WithoutHardwareSamples_ReturnsUnavailable()
    {
        var provider = new WindowsSystemTelemetryProvider(new FakeSource());

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Unavailable, sample.Status);
        Assert.Equal(
            TelemetryReasonCodes.SystemPerformanceCounterNoSamples,
            sample.ReasonCode);
        Assert.Empty(sample.Metrics);
    }

    [Fact]
    public async Task CollectAsync_WhenCounterQueryFails_ReturnsStableFailureReason()
    {
        var provider = new WindowsSystemTelemetryProvider(new FakeSource
        {
            CollectionException = new InvalidOperationException("Counter provider failed.")
        });

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Failed, sample.Status);
        Assert.Equal(
            TelemetryReasonCodes.SystemPerformanceCounterQueryFailed,
            sample.ReasonCode);
        Assert.Contains("Counter provider failed", sample.TechnicalMessage);
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

    private sealed class FakeSource : IWindowsSystemPerformanceCounterSource
    {
        public WindowsSystemPerformanceCounterAvailability Availability { get; init; } =
            new()
            {
                SupportsCpuFrequency = true,
                SupportsThermalZoneTemperature = true
            };

        public WindowsSystemPerformanceSnapshot Snapshot { get; init; } = new();

        public Exception? CollectionException { get; init; }

        public Task<WindowsSystemPerformanceCounterAvailability> GetAvailabilityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<WindowsSystemPerformanceSnapshot> CollectAsync(
            CancellationToken cancellationToken = default) =>
            CollectionException is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<WindowsSystemPerformanceSnapshot>(CollectionException);
    }
}
