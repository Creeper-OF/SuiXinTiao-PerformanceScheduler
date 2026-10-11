using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

namespace PerformanceScheduler.Tests;

public sealed class WindowsGpuProcessTelemetryProviderTests
{
    [Fact]
    public async Task GetCapabilitiesAsync_ReflectsAvailableCounterGroups()
    {
        var source = new FakeSource
        {
            Availability = new WindowsGpuPerformanceCounterAvailability
            {
                SupportsEngineUtilization = true,
                SupportsProcessMemory = false
            }
        };
        var provider = new WindowsGpuProcessTelemetryProvider(source);

        var capabilities = await provider.GetCapabilitiesAsync();

        Assert.True(capabilities.IsSupported);
        Assert.Equal([TelemetryMetricKind.ProcessGpuUsagePercent], capabilities.SupportedMetrics);
        Assert.Equal(TimeSpan.FromSeconds(2), capabilities.MinimumSamplingInterval);
    }

    [Fact]
    public async Task CollectAsync_ConvertsProcessGpuMetrics()
    {
        var source = new FakeSource
        {
            Snapshot = new GpuProcessPerformanceSnapshot
            {
                UsagePercent = 64.5,
                DedicatedMemoryBytes = 512 * 1024 * 1024,
                SharedMemoryBytes = 128 * 1024 * 1024
            }
        };
        var provider = new WindowsGpuProcessTelemetryProvider(source);

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Collection(
            sample.Metrics,
            metric => AssertMetric(metric, TelemetryMetricKind.ProcessGpuUsagePercent, 64.5, "%"),
            metric => AssertMetric(metric, TelemetryMetricKind.ProcessGpuDedicatedMemoryMegabytes, 512, "MiB"),
            metric => AssertMetric(metric, TelemetryMetricKind.ProcessGpuSharedMemoryMegabytes, 128, "MiB"));
        Assert.Equal(42, source.LastProcessId);
    }

    [Fact]
    public async Task CollectAsync_WithoutProcessGpuInstances_ReturnsUnavailable()
    {
        var provider = new WindowsGpuProcessTelemetryProvider(new FakeSource());

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Unavailable, sample.Status);
        Assert.Equal(TelemetryReasonCodes.GpuProcessNoSamples, sample.ReasonCode);
        Assert.Empty(sample.Metrics);
    }

    [Fact]
    public async Task CollectAsync_WhenCounterQueryFails_ReturnsStableFailureReason()
    {
        var provider = new WindowsGpuProcessTelemetryProvider(new FakeSource
        {
            CollectionException = new InvalidOperationException("Counter provider failed.")
        });

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Failed, sample.Status);
        Assert.Equal(TelemetryReasonCodes.GpuPerformanceCounterQueryFailed, sample.ReasonCode);
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

    private sealed class FakeSource : IWindowsGpuPerformanceCounterSource
    {
        public WindowsGpuPerformanceCounterAvailability Availability { get; init; } =
            new()
            {
                SupportsEngineUtilization = true,
                SupportsProcessMemory = true
            };

        public GpuProcessPerformanceSnapshot Snapshot { get; init; } = new();

        public Exception? CollectionException { get; init; }

        public int LastProcessId { get; private set; }

        public Task<WindowsGpuPerformanceCounterAvailability> GetAvailabilityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<GpuProcessPerformanceSnapshot> CollectProcessAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            LastProcessId = processId;
            return CollectionException is null
                ? Task.FromResult(Snapshot)
                : Task.FromException<GpuProcessPerformanceSnapshot>(CollectionException);
        }
    }
}
