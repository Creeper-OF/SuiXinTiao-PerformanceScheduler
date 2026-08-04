using Microsoft.Data.Sqlite;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class SqliteTelemetryHistoryStore : ITelemetryHistoryStore
{
    private readonly string _connectionString;

    public SqliteTelemetryHistoryStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS telemetry_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at TEXT NOT NULL,
                total_duration_ms REAL NOT NULL,
                process_id INTEGER NOT NULL,
                process_name TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS telemetry_provider_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observation_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                provider_id TEXT NOT NULL,
                collected_at TEXT NOT NULL,
                status TEXT NOT NULL,
                collection_duration_ms REAL NOT NULL,
                reason_code TEXT NOT NULL,
                technical_message TEXT NOT NULL,
                FOREIGN KEY (observation_id) REFERENCES telemetry_observations(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS telemetry_metrics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                provider_sample_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                value REAL NOT NULL,
                unit TEXT NOT NULL,
                FOREIGN KEY (provider_sample_id) REFERENCES telemetry_provider_samples(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_telemetry_observations_started_at
                ON telemetry_observations(started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_telemetry_provider_samples_observation
                ON telemetry_provider_samples(observation_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_telemetry_metrics_provider_sample
                ON telemetry_metrics(provider_sample_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordObservationAsync(
        TelemetryObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var observationId = await InsertObservationAsync(connection, transaction, observation, cancellationToken);
        for (var providerIndex = 0; providerIndex < observation.ProviderSamples.Count; providerIndex++)
        {
            var sample = observation.ProviderSamples[providerIndex];
            var providerSampleId = await InsertProviderSampleAsync(
                connection,
                transaction,
                observationId,
                providerIndex,
                sample,
                cancellationToken);
            for (var metricIndex = 0; metricIndex < sample.Metrics.Count; metricIndex++)
            {
                await InsertMetricAsync(
                    connection,
                    transaction,
                    providerSampleId,
                    metricIndex,
                    sample.Metrics[metricIndex],
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TelemetryObservationHistoryEntry>> LoadRecentAsync(
        int count = 100,
        CancellationToken cancellationToken = default)
    {
        var limitedCount = Math.Clamp(count, 1, 1000);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH recent_observations AS (
                SELECT *
                FROM telemetry_observations
                ORDER BY id DESC
                LIMIT $count
            )
            SELECT
                o.id,
                o.started_at,
                o.total_duration_ms,
                o.process_id,
                o.process_name,
                p.id,
                p.provider_id,
                p.collected_at,
                p.status,
                p.collection_duration_ms,
                p.reason_code,
                p.technical_message,
                m.kind,
                m.value,
                m.unit
            FROM recent_observations o
            LEFT JOIN telemetry_provider_samples p ON p.observation_id = o.id
            LEFT JOIN telemetry_metrics m ON m.provider_sample_id = p.id
            ORDER BY o.id DESC, p.sequence ASC, m.sequence ASC;
            """;
        command.Parameters.AddWithValue("$count", limitedCount);

        var observations = new List<ObservationBuilder>();
        var observationsById = new Dictionary<long, ObservationBuilder>();
        var providersById = new Dictionary<long, ProviderSampleBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var observationId = reader.GetInt64(0);
            if (!observationsById.TryGetValue(observationId, out var observation))
            {
                observation = CreateObservationBuilder(reader, observationId);
                observationsById.Add(observationId, observation);
                observations.Add(observation);
            }

            if (reader.IsDBNull(5))
            {
                continue;
            }

            var providerSampleId = reader.GetInt64(5);
            if (!providersById.TryGetValue(providerSampleId, out var provider))
            {
                provider = CreateProviderBuilder(reader);
                providersById.Add(providerSampleId, provider);
                observation.Providers.Add(provider);
            }

            if (TryReadMetric(reader) is { } metric)
            {
                provider.Metrics.Add(metric);
            }
        }

        return observations.Select(builder => builder.Build()).ToArray();
    }

    public async Task<int> PruneAsync(
        TelemetryHistoryRetentionPolicy policy,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = policy.Normalize();
        var cutoff = (now ?? DateTimeOffset.UtcNow) - normalized.MaximumAge;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM telemetry_observations
            WHERE id IN (
                SELECT id
                FROM telemetry_observations
                WHERE started_at < $cutoff
                   OR id NOT IN (
                       SELECT id
                       FROM telemetry_observations
                       ORDER BY id DESC
                       LIMIT $maximumObservations
                   )
                ORDER BY id ASC
                LIMIT $deleteBatchSize
            );
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$maximumObservations", normalized.MaximumObservations);
        command.Parameters.AddWithValue("$deleteBatchSize", normalized.DeleteBatchSize);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TelemetryObservation observation,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO telemetry_observations (
                started_at,
                total_duration_ms,
                process_id,
                process_name
            ) VALUES ($startedAt, $totalDurationMs, $processId, $processName);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$startedAt", observation.StartedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$totalDurationMs", observation.TotalDuration.TotalMilliseconds);
        command.Parameters.AddWithValue("$processId", observation.Target.ProcessId);
        command.Parameters.AddWithValue("$processName", observation.Target.ProcessName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertProviderSampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long observationId,
        int sequence,
        TelemetryProviderSample sample,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO telemetry_provider_samples (
                observation_id,
                sequence,
                provider_id,
                collected_at,
                status,
                collection_duration_ms,
                reason_code,
                technical_message
            ) VALUES (
                $observationId,
                $sequence,
                $providerId,
                $collectedAt,
                $status,
                $collectionDurationMs,
                $reasonCode,
                $technicalMessage
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$observationId", observationId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$providerId", sample.ProviderId);
        command.Parameters.AddWithValue("$collectedAt", sample.CollectedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$status", sample.Status.ToString());
        command.Parameters.AddWithValue("$collectionDurationMs", sample.CollectionDuration.TotalMilliseconds);
        command.Parameters.AddWithValue("$reasonCode", sample.ReasonCode);
        command.Parameters.AddWithValue("$technicalMessage", sample.TechnicalMessage);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertMetricAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long providerSampleId,
        int sequence,
        TelemetryMetricValue metric,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO telemetry_metrics (
                provider_sample_id,
                sequence,
                kind,
                value,
                unit
            ) VALUES ($providerSampleId, $sequence, $kind, $value, $unit);
            """;
        command.Parameters.AddWithValue("$providerSampleId", providerSampleId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$kind", metric.Kind.ToString());
        command.Parameters.AddWithValue("$value", metric.Value);
        command.Parameters.AddWithValue("$unit", metric.Unit);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ObservationBuilder CreateObservationBuilder(SqliteDataReader reader, long storageId) =>
        new(
            storageId,
            ParseTimestamp(reader.GetString(1)),
            TimeSpan.FromMilliseconds(reader.GetDouble(2)),
            checked((int)reader.GetInt64(3)),
            reader.GetString(4));

    private static ProviderSampleBuilder CreateProviderBuilder(SqliteDataReader reader) =>
        new(
            reader.GetString(6),
            ParseTimestamp(reader.GetString(7)),
            Enum.TryParse<TelemetryCollectionStatus>(reader.GetString(8), ignoreCase: true, out var status)
                ? status
                : TelemetryCollectionStatus.Failed,
            TimeSpan.FromMilliseconds(reader.GetDouble(9)),
            reader.GetString(10),
            reader.GetString(11));

    private static TelemetryMetricValue? TryReadMetric(SqliteDataReader reader)
    {
        if (reader.IsDBNull(12) ||
            !Enum.TryParse<TelemetryMetricKind>(reader.GetString(12), ignoreCase: true, out var kind))
        {
            return null;
        }

        return new TelemetryMetricValue
        {
            Kind = kind,
            Value = reader.GetDouble(13),
            Unit = reader.GetString(14)
        };
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private SqliteConnection CreateConnection() => new(_connectionString);

    private sealed class ObservationBuilder(
        long storageId,
        DateTimeOffset startedAt,
        TimeSpan totalDuration,
        int processId,
        string processName)
    {
        public List<ProviderSampleBuilder> Providers { get; } = new();

        public TelemetryObservationHistoryEntry Build() =>
            new()
            {
                StorageId = storageId,
                Observation = new TelemetryObservation
                {
                    StartedAt = startedAt,
                    TotalDuration = totalDuration,
                    Target = new FocusedAppContext
                    {
                        ProcessId = processId,
                        ProcessName = processName,
                        WindowTitle = string.Empty,
                        WindowHandle = nint.Zero
                    },
                    ProviderSamples = Providers.Select(provider => provider.Build()).ToArray()
                }
            };
    }

    private sealed class ProviderSampleBuilder(
        string providerId,
        DateTimeOffset collectedAt,
        TelemetryCollectionStatus status,
        TimeSpan collectionDuration,
        string reasonCode,
        string technicalMessage)
    {
        public List<TelemetryMetricValue> Metrics { get; } = new();

        public TelemetryProviderSample Build() =>
            new()
            {
                ProviderId = providerId,
                CollectedAt = collectedAt,
                Status = status,
                CollectionDuration = collectionDuration,
                ReasonCode = reasonCode,
                TechnicalMessage = technicalMessage,
                Metrics = Metrics.ToArray()
            };
    }
}
