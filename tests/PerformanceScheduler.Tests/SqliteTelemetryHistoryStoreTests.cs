using Microsoft.Data.Sqlite;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class SqliteTelemetryHistoryStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _databasePath;

    public SqliteTelemetryHistoryStoreTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "PerformanceScheduler.Tests",
            Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_tempDirectory, "scheduler.db");
    }

    [Fact]
    public async Task RecordObservationAsync_PersistsProviderSamplesAndMetrics()
    {
        var store = new SqliteTelemetryHistoryStore(_databasePath);
        await store.InitializeAsync();
        var observation = CreateObservation(
            new DateTimeOffset(2026, 8, 4, 12, 30, 0, TimeSpan.Zero),
            processId: 42,
            processName: "game");

        await store.RecordObservationAsync(observation);

        var history = await store.LoadRecentAsync();
        var entry = Assert.Single(history);
        Assert.True(entry.StorageId > 0);
        Assert.Equal(observation.StartedAt, entry.Observation.StartedAt);
        Assert.Equal(observation.TotalDuration, entry.Observation.TotalDuration);
        Assert.Equal(42, entry.Observation.Target.ProcessId);
        Assert.Equal("game", entry.Observation.Target.ProcessName);
        Assert.Equal(string.Empty, entry.Observation.Target.WindowTitle);
        var sample = Assert.Single(entry.Observation.ProviderSamples);
        Assert.Equal("process", sample.ProviderId);
        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Equal("sample-ready", sample.ReasonCode);
        Assert.Equal("Collected from process counters.", sample.TechnicalMessage);
        Assert.Collection(
            sample.Metrics,
            metric =>
            {
                Assert.Equal(TelemetryMetricKind.ProcessCpuUsagePercent, metric.Kind);
                Assert.Equal(27.5, metric.Value);
                Assert.Equal("%", metric.Unit);
            },
            metric =>
            {
                Assert.Equal(TelemetryMetricKind.WorkingSetMegabytes, metric.Kind);
                Assert.Equal(512, metric.Value);
                Assert.Equal("MiB", metric.Unit);
            });
    }

    [Fact]
    public async Task LoadRecentAsync_ReturnsNewestObservationsFirstAndRespectsCount()
    {
        var store = new SqliteTelemetryHistoryStore(_databasePath);
        await store.InitializeAsync();
        var startedAt = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        await store.RecordObservationAsync(CreateObservation(startedAt, 1, "first"));
        await store.RecordObservationAsync(CreateObservation(startedAt.AddMinutes(1), 2, "second"));
        await store.RecordObservationAsync(CreateObservation(startedAt.AddMinutes(2), 3, "third"));

        var history = await store.LoadRecentAsync(2);

        Assert.Equal(2, history.Count);
        Assert.Equal("third", history[0].Observation.Target.ProcessName);
        Assert.Equal("second", history[1].Observation.Target.ProcessName);
    }

    [Fact]
    public async Task PruneAsync_DeletesAtMostOneBatchAndCascadesDetails()
    {
        var store = new SqliteTelemetryHistoryStore(_databasePath);
        await store.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 15; index++)
        {
            await store.RecordObservationAsync(
                CreateObservation(now.AddDays(-30).AddMinutes(index), index + 1, $"old-{index}"));
        }

        await store.RecordObservationAsync(CreateObservation(now, 100, "current"));
        var policy = new TelemetryHistoryRetentionPolicy
        {
            MaximumAge = TimeSpan.FromDays(7),
            MaximumObservations = 1000,
            DeleteBatchSize = 10
        };

        var firstDeleted = await store.PruneAsync(policy, now);
        var secondDeleted = await store.PruneAsync(policy, now);

        Assert.Equal(10, firstDeleted);
        Assert.Equal(5, secondDeleted);
        var history = await store.LoadRecentAsync(100);
        var remaining = Assert.Single(history);
        Assert.Equal("current", remaining.Observation.Target.ProcessName);
        Assert.Equal((1L, 1L, 2L), await ReadRowCountsAsync());
    }

    [Fact]
    public async Task PruneAsync_RemovesObservationsBeyondMaximumCount()
    {
        var store = new SqliteTelemetryHistoryStore(_databasePath);
        await store.InitializeAsync();
        var startedAt = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 105; index++)
        {
            await store.RecordObservationAsync(
                CreateObservation(startedAt.AddSeconds(index), index + 1, $"process-{index}"));
        }

        var deleted = await store.PruneAsync(
            new TelemetryHistoryRetentionPolicy
            {
                MaximumAge = TimeSpan.FromDays(30),
                MaximumObservations = 100,
                DeleteBatchSize = 10
            },
            startedAt.AddDays(1));

        Assert.Equal(5, deleted);
        var history = await store.LoadRecentAsync(1000);
        Assert.Equal(100, history.Count);
        Assert.Equal("process-104", history[0].Observation.Target.ProcessName);
        Assert.Equal("process-5", history[^1].Observation.Target.ProcessName);
    }

    private async Task<(long Observations, long Providers, long Metrics)> ReadRowCountsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM telemetry_observations),
                (SELECT COUNT(*) FROM telemetry_provider_samples),
                (SELECT COUNT(*) FROM telemetry_metrics);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static TelemetryObservation CreateObservation(
        DateTimeOffset startedAt,
        int processId,
        string processName) =>
        new()
        {
            StartedAt = startedAt,
            TotalDuration = TimeSpan.FromMilliseconds(25),
            Target = new FocusedAppContext
            {
                ProcessId = processId,
                ProcessName = processName,
                WindowTitle = $"{processName} window",
                WindowHandle = (nint)processId,
                ExecutablePath = $"C:\\Apps\\{processName}.exe"
            },
            ProviderSamples =
            [
                new TelemetryProviderSample
                {
                    ProviderId = "process",
                    CollectedAt = startedAt.AddMilliseconds(10),
                    Status = TelemetryCollectionStatus.Collected,
                    CollectionDuration = TimeSpan.FromMilliseconds(5),
                    ReasonCode = "sample-ready",
                    TechnicalMessage = "Collected from process counters.",
                    Metrics =
                    [
                        new TelemetryMetricValue
                        {
                            Kind = TelemetryMetricKind.ProcessCpuUsagePercent,
                            Value = 27.5,
                            Unit = "%"
                        },
                        new TelemetryMetricValue
                        {
                            Kind = TelemetryMetricKind.WorkingSetMegabytes,
                            Value = 512,
                            Unit = "MiB"
                        }
                    ]
                }
            ]
        };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.DeleteWithRetry(_tempDirectory);
    }
}
