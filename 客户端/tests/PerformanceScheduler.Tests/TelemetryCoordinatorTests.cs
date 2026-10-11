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
    public void Observation_WithOnlyNonCollectedMetrics_HasNoMetrics()
    {
        var observation = new TelemetryObservation
        {
            Target = CreateTarget(),
            ProviderSamples =
            [
                new TelemetryProviderSample
                {
                    ProviderId = "failed-provider",
                    Status = TelemetryCollectionStatus.Failed,
                    Metrics =
                    [
                        new TelemetryMetricValue
                        {
                            Kind = TelemetryMetricKind.ProcessCpuUsagePercent,
                            Value = 80,
                            Unit = "%"
                        }
                    ]
                }
            ]
        };

        Assert.False(observation.HasMetrics);
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
            CollectAsyncHandler = _ => Task.Delay(Timeout.InfiniteTimeSpan)
                .ContinueWith(
                    _ => new TelemetryProviderSample { ProviderId = "test" },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
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
    public async Task ObserveAsync_WhenCallerCancelsBeforeUncooperativeProvider_PropagatesCancellation()
    {
        var provider = new RecordingProvider
        {
            CapabilitiesAsyncHandler = _ => Task.Delay(Timeout.InfiniteTimeSpan)
                .ContinueWith(
                    _ => new TelemetryProviderCapabilities
                    {
                        ProviderId = "test",
                        IsSupported = true
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
        };
        var coordinator = new TelemetryCoordinator(
            [provider],
            new TelemetryCollectionBudget
            {
                MaximumProviderDuration = TimeSpan.FromSeconds(2),
                MaximumObservationDuration = TimeSpan.FromSeconds(3)
            });
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ObserveAsync(CreateTarget(), cancellationSource.Token));
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

    [Fact]
    public async Task ObserveAsync_WhenCapabilityProviderIdDoesNotMatch_RejectsProvider()
    {
        var provider = new RecordingProvider
        {
            CapabilityProviderId = "different-provider"
        };
        var coordinator = new TelemetryCoordinator([provider]);

        var observation = await coordinator.ObserveAsync(CreateTarget());

        var sample = Assert.Single(observation.ProviderSamples);
        Assert.Equal(TelemetryCollectionStatus.Failed, sample.Status);
        Assert.Equal(TelemetryReasonCodes.CapabilityFailed, sample.ReasonCode);
        Assert.Equal(0, provider.CollectionCount);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_ReturnsCachedProviderMetadataWithoutCollecting()
    {
        var provider = new RecordingProvider
        {
            MinimumSamplingInterval = TimeSpan.FromSeconds(2)
        };
        var coordinator = new TelemetryCoordinator([provider]);

        var first = await coordinator.GetCapabilitiesAsync();
        var second = await coordinator.GetCapabilitiesAsync();

        var capability = Assert.Single(first);
        Assert.Equal(provider.ProviderId, capability.ProviderId);
        Assert.True(capability.IsSupported);
        Assert.Equal(TimeSpan.FromSeconds(2), capability.MinimumSamplingInterval);
        Assert.Equal(provider.ProviderId, Assert.Single(second).ProviderId);
        Assert.Equal(1, provider.CapabilityCheckCount);
        Assert.Equal(0, provider.CollectionCount);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenProviderTimesOut_ReturnsUnsupportedCapability()
    {
        var provider = new RecordingProvider
        {
            CapabilitiesAsyncHandler = _ => Task.Delay(Timeout.InfiniteTimeSpan)
                .ContinueWith(
                    _ => new TelemetryProviderCapabilities
                    {
                        ProviderId = "test",
                        IsSupported = true
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
        };
        var coordinator = new TelemetryCoordinator(
            [provider],
            new TelemetryCollectionBudget
            {
                MaximumProviderDuration = TimeSpan.FromMilliseconds(50),
                MaximumObservationDuration = TimeSpan.FromMilliseconds(100)
            });

        var capabilities = await coordinator.GetCapabilitiesAsync();

        var capability = Assert.Single(capabilities);
        Assert.Equal(provider.ProviderId, capability.ProviderId);
        Assert.False(capability.IsSupported);
        Assert.Equal(TelemetryReasonCodes.CapabilityTimeout, capability.UnavailableReasonCode);
        Assert.Equal(1, provider.CapabilityCheckCount);
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

        public string CapabilityProviderId { get; set; } = "test";

        public Func<CancellationToken, Task<TelemetryProviderCapabilities>>? CapabilitiesAsyncHandler { get; init; }

        public Func<CancellationToken, Task<TelemetryProviderSample>>? CollectAsyncHandler { get; init; }

        public Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            CapabilityCheckCount++;
            if (CapabilitiesAsyncHandler is not null)
            {
                return CapabilitiesAsyncHandler(cancellationToken);
            }

            return Task.FromResult(new TelemetryProviderCapabilities
            {
                ProviderId = CapabilityProviderId,
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
