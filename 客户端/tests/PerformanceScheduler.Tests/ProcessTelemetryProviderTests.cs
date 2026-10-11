using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Telemetry;

namespace PerformanceScheduler.Tests;

public sealed class ProcessTelemetryProviderTests
{
    [Fact]
    public async Task CollectAsync_MapsAvailableProcessMetrics()
    {
        var provider = new ProcessTelemetryProvider(new FakeMetricsCollector(new MetricsSnapshot
        {
            CpuUsagePercent = 12.5,
            WorkingSetMb = 256,
            PrivateMemoryMb = 192,
            ThreadCount = 18,
            HandleCount = 240
        }));

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Equal(5, sample.Metrics.Count);
        Assert.Contains(sample.Metrics, metric =>
            metric.Kind == TelemetryMetricKind.ProcessCpuUsagePercent && metric.Value == 12.5);
    }

    [Fact]
    public async Task CollectAsync_FirstCpuSample_ReturnsWarmupWithOtherMetrics()
    {
        var provider = new ProcessTelemetryProvider(new FakeMetricsCollector(new MetricsSnapshot
        {
            CpuUsagePercent = null,
            WorkingSetMb = 64,
            ThreadCount = 4,
            HandleCount = 20
        }));

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Warmup, sample.Status);
        Assert.Equal(TelemetryReasonCodes.CpuWarmup, sample.ReasonCode);
        Assert.DoesNotContain(sample.Metrics, metric => metric.Kind == TelemetryMetricKind.ProcessCpuUsagePercent);
        Assert.NotEmpty(sample.Metrics);
    }

    private static FocusedAppContext CreateTarget() =>
        new()
        {
            ProcessId = 42,
            ProcessName = "sample",
            WindowTitle = "Sample",
            WindowHandle = (nint)1
        };

    private sealed class FakeMetricsCollector(MetricsSnapshot snapshot) : IMetricsCollector
    {
        public Task<MetricsSnapshot> CollectAsync(
            FocusedAppContext app,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
}
