using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class TelemetryCoordinatorTests
{
    [Fact]
    public async Task ObserveAsync_CollectsSupportedProviderMetrics()
    {
        var provider = new RecordingProvider();
        var coordinator = new TelemetryCoordinator([provider]);

        var observation = await coordinator.ObserveAsync(CreateTarget());

        var sample = Assert.Single(observation.ProviderSamples);
        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Single(sample.Metrics);
        Assert.True(observation.HasMetrics);
        Assert.Equal(1, provider.CollectionCount);
        Assert.Equal(1, provider.CapabilityCheckCount);
    }

    [Fact]
    public async Task ObserveAsync_WithinMinimumInterval_DoesNotCallProviderAgain()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new RecordingProvider
        {
            MinimumSamplingInterval = TimeSpan.FromSeconds(2)
        };
        var coordinator = new TelemetryCoordinator(
            [provider],
            utcNow: () => now);

        await coordinator.ObserveAsync(CreateTarget());
        now += TimeSpan.FromSeconds(1);
        var second = await coordinator.ObserveAsync(CreateTarget());

        Assert.Equal(1, provider.CollectionCount);
        Assert.Equal(1, provider.CapabilityCheckCount);
        var sample = Assert.Single(second.ProviderSamples);
        Assert.Equal(TelemetryCollectionStatus.RateLimited, sample.Status);
        Assert.Equal(TelemetryReasonCodes.MinimumIntervalNotElapsed, sample.ReasonCode);
    }

    [Fact]
    public async Task ObserveAsync_WhenProviderTimesOut_ReturnsTimedOutStatus()
    {
        var provider = new RecordingProvider
        {
            CollectAsyncHandler = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new TelemetryProviderSample { ProviderId = "test" };
            }
        };
        var coordinator = new TelemetryCoordinator(
            [provider],
            new TelemetryCollectionBudget
            {
                MaximumProviderDuration = TimeSpan.FromMilliseconds(50),
                MaximumObservationDuration = TimeSpan.FromMilliseconds(100)
            });

        var observation = await coordinator.ObserveAsync(CreateTarget());

        var sample = Assert.Single(observation.ProviderSamples);
        Assert.Equal(TelemetryCollectionStatus.TimedOut, sample.Status);
        Assert.Equal(TelemetryReasonCodes.CollectionTimeout, sample.ReasonCode);
    }

    [Fact]
    public void Constructor_WithDuplicateProviderIds_RejectsAmbiguousProviders()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new TelemetryCoordinator([new RecordingProvider(), new RecordingProvider()]));

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidateCapabilities_RechecksPreviouslyUnsupportedProvider()
    {
        var provider = new RecordingProvider { IsSupported = false };
        var coordinator = new TelemetryCoordinator([provider]);
        var first = await coordinator.ObserveAsync(CreateTarget());
        provider.IsSupported = true;

        var stillCached = await coordinator.ObserveAsync(CreateTarget());
        coordinator.InvalidateCapabilities(provider.ProviderId);
        var refreshed = await coordinator.ObserveAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Unavailable, Assert.Single(first.ProviderSamples).Status);
        Assert.Equal(TelemetryCollectionStatus.Unavailable, Assert.Single(stillCached.ProviderSamples).Status);
        Assert.Equal(TelemetryCollectionStatus.Collected, Assert.Single(refreshed.ProviderSamples).Status);
        Assert.Equal(2, provider.CapabilityCheckCount);
    }

    private static FocusedAppContext CreateTarget() =>
        new()
        {
            ProcessId = 42,
            ProcessName = "sample",
            WindowTitle = "Sample",
            WindowHandle = (nint)1
        };

    private sealed class RecordingProvider : ITelemetryProvider
    {
        public string ProviderId => "test";

        public int CollectionCount { get; private set; }

        public int CapabilityCheckCount { get; private set; }

        public TimeSpan MinimumSamplingInterval { get; init; } = TimeSpan.FromMilliseconds(500);

        public bool IsSupported { get; set; } = true;

        public Func<CancellationToken, Task<TelemetryProviderSample>>? CollectAsyncHandler { get; init; }

        public Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            CapabilityCheckCount++;
            return Task.FromResult(new TelemetryProviderCapabilities
            {
                ProviderId = ProviderId,
                IsSupported = IsSupported,
                SupportedMetrics = [TelemetryMetricKind.WorkingSetMegabytes],
                MinimumSamplingInterval = MinimumSamplingInterval
            });
        }

        public async Task<TelemetryProviderSample> CollectAsync(
            FocusedAppContext target,
            CancellationToken cancellationToken = default)
        {
            CollectionCount++;
            if (CollectAsyncHandler is not null)
            {
                return await CollectAsyncHandler(cancellationToken);
            }

            return new TelemetryProviderSample
            {
                ProviderId = ProviderId,
                Status = TelemetryCollectionStatus.Collected,
                Metrics =
                [
                    new TelemetryMetricValue
                    {
                        Kind = TelemetryMetricKind.WorkingSetMegabytes,
                        Value = 128,
                        Unit = "MiB"
                    }
                ]
            };
        }
    }
}
