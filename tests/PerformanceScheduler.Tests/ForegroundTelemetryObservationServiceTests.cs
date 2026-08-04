using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ForegroundTelemetryObservationServiceTests
{
    [Fact]
    public async Task CaptureAsync_WithoutForeground_DoesNotInvokeTelemetryProvider()
    {
        var provider = new RecordingProvider();
        var service = new ForegroundTelemetryObservationService(
            new FakeForegroundWatcher(null),
            new FakeProcessInspector(null),
            new TelemetryCoordinator([provider]));

        var result = await service.CaptureAsync();

        Assert.Equal(ForegroundTelemetryObservationStatus.ForegroundUnavailable, result.Status);
        Assert.Equal(0, provider.CollectionCount);
    }

    [Fact]
    public async Task CaptureAsync_WithInspectableTarget_ReturnsObservation()
    {
        var target = new FocusedAppContext
        {
            ProcessId = 42,
            ProcessName = "sample",
            WindowTitle = "Sample",
            WindowHandle = (nint)1
        };
        var provider = new RecordingProvider();
        var service = new ForegroundTelemetryObservationService(
            new FakeForegroundWatcher(new ForegroundWindowSnapshot((nint)1, 42, "Sample")),
            new FakeProcessInspector(target),
            new TelemetryCoordinator([provider]));

        var result = await service.CaptureAsync();

        Assert.Equal(ForegroundTelemetryObservationStatus.Collected, result.Status);
        Assert.Same(target, result.Target);
        Assert.NotNull(result.Observation);
        Assert.Equal(1, provider.CollectionCount);
    }

    [Fact]
    public async Task CaptureAsync_WithHistoryStore_RecordsAndPrunesObservation()
    {
        var target = CreateTarget();
        var historyStore = new RecordingHistoryStore();
        var service = new ForegroundTelemetryObservationService(
            new FakeForegroundWatcher(new ForegroundWindowSnapshot((nint)1, 42, "Sample")),
            new FakeProcessInspector(target),
            new TelemetryCoordinator([new RecordingProvider()]),
            historyStore);

        var result = await service.CaptureAsync();

        Assert.Equal(ForegroundTelemetryObservationStatus.Collected, result.Status);
        Assert.True(result.HistoryRecorded);
        Assert.Same(result.Observation, historyStore.RecordedObservation);
        Assert.Equal(1, historyStore.PruneCount);
    }

    [Fact]
    public async Task CaptureAsync_WhenHistoryStoreFails_StillReturnsObservation()
    {
        var target = CreateTarget();
        var service = new ForegroundTelemetryObservationService(
            new FakeForegroundWatcher(new ForegroundWindowSnapshot((nint)1, 42, "Sample")),
            new FakeProcessInspector(target),
            new TelemetryCoordinator([new RecordingProvider()]),
            new ThrowingHistoryStore());

        var result = await service.CaptureAsync();

        Assert.Equal(ForegroundTelemetryObservationStatus.Collected, result.Status);
        Assert.NotNull(result.Observation);
        Assert.False(result.HistoryRecorded);
    }

    private static FocusedAppContext CreateTarget() =>
        new()
        {
            ProcessId = 42,
            ProcessName = "sample",
            WindowTitle = "Sample",
            WindowHandle = (nint)1
        };

    private sealed class FakeForegroundWatcher(ForegroundWindowSnapshot? snapshot) : IForegroundWatcher
    {
        public Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FakeProcessInspector(FocusedAppContext? target) : IProcessInspector
    {
        public Task<FocusedAppContext?> InspectAsync(
            ForegroundWindowSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(target);
    }

    private sealed class RecordingProvider : ITelemetryProvider
    {
        public string ProviderId => "test";

        public int CollectionCount { get; private set; }

        public Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelemetryProviderCapabilities
            {
                ProviderId = ProviderId,
                IsSupported = true,
                SupportedMetrics = [TelemetryMetricKind.WorkingSetMegabytes]
            });

        public Task<TelemetryProviderSample> CollectAsync(
            FocusedAppContext target,
            CancellationToken cancellationToken = default)
        {
            CollectionCount++;
            return Task.FromResult(new TelemetryProviderSample
            {
                ProviderId = ProviderId,
                Status = TelemetryCollectionStatus.Collected,
                Metrics =
                [
                    new TelemetryMetricValue
                    {
                        Kind = TelemetryMetricKind.WorkingSetMegabytes,
                        Value = 32,
                        Unit = "MiB"
                    }
                ]
            });
        }
    }

    private sealed class RecordingHistoryStore : ITelemetryHistoryStore
    {
        public TelemetryObservation? RecordedObservation { get; private set; }

        public int PruneCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordObservationAsync(
            TelemetryObservation observation,
            CancellationToken cancellationToken = default)
        {
            RecordedObservation = observation;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelemetryObservationHistoryEntry>> LoadRecentAsync(
            int count = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TelemetryObservationHistoryEntry>>([]);

        public Task<int> PruneAsync(
            TelemetryHistoryRetentionPolicy policy,
            DateTimeOffset? now = null,
            CancellationToken cancellationToken = default)
        {
            PruneCount++;
            return Task.FromResult(0);
        }
    }

    private sealed class ThrowingHistoryStore : ITelemetryHistoryStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordObservationAsync(
            TelemetryObservation observation,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("History unavailable."));

        public Task<IReadOnlyList<TelemetryObservationHistoryEntry>> LoadRecentAsync(
            int count = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TelemetryObservationHistoryEntry>>([]);

        public Task<int> PruneAsync(
            TelemetryHistoryRetentionPolicy policy,
            DateTimeOffset? now = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
