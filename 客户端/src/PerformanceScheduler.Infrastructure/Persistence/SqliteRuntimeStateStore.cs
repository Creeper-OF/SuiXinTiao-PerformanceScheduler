using Microsoft.Data.Sqlite;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class SqliteRuntimeStateStore : IRuntimeStateStore
{
    private readonly string _databasePath;

    public SqliteRuntimeStateStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        FileStoreSnapshot.EnsureSafeFilePath(_databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS scheduler_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                process_name TEXT NULL,
                window_title TEXT NULL,
                profile_id TEXT NULL,
                profile_name TEXT NULL,
                power_plan_guid TEXT NULL,
                power_plan_name TEXT NULL,
                priority TEXT NULL,
                success INTEGER NOT NULL,
                is_retry_deferred INTEGER NOT NULL DEFAULT 0,
                consecutive_failure_count INTEGER NOT NULL DEFAULT 0,
                retry_after TEXT NULL,
                failure_recovery_completed INTEGER NULL,
                summary TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS scheduler_run_actions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scheduler_run_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                action_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                target TEXT NOT NULL,
                original_value TEXT NOT NULL,
                target_value TEXT NOT NULL,
                status TEXT NOT NULL,
                reason_code TEXT NOT NULL DEFAULT 'None',
                reason TEXT NOT NULL,
                message TEXT NOT NULL,
                recovery_description TEXT NOT NULL,
                requires_rollback INTEGER NOT NULL,
                completed_at TEXT NOT NULL,
                FOREIGN KEY (scheduler_run_id) REFERENCES scheduler_runs(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_scheduler_run_actions_run_id
                ON scheduler_run_actions(scheduler_run_id, sequence);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_runs",
            columnName: "run_id",
            definition: "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_runs",
            columnName: "transaction_id",
            definition: "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_run_actions",
            columnName: "reason_code",
            definition: "TEXT NOT NULL DEFAULT 'None'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_runs",
            columnName: "is_retry_deferred",
            definition: "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_runs",
            columnName: "consecutive_failure_count",
            definition: "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_runs",
            columnName: "retry_after",
            definition: "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            tableName: "scheduler_runs",
            columnName: "failure_recovery_completed",
            definition: "INTEGER NULL",
            cancellationToken);

        var indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_scheduler_runs_run_id
                ON scheduler_runs(run_id)
                WHERE run_id IS NOT NULL;
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO scheduler_runs (
                run_id,
                transaction_id,
                occurred_at,
                process_name,
                window_title,
                profile_id,
                profile_name,
                power_plan_guid,
                power_plan_name,
                priority,
                success,
                is_retry_deferred,
                consecutive_failure_count,
                retry_after,
                failure_recovery_completed,
                summary
            )
            VALUES (
                $runId,
                $transactionId,
                $occurredAt,
                $processName,
                $windowTitle,
                $profileId,
                $profileName,
                $powerPlanGuid,
                $powerPlanName,
                $priority,
                $success,
                $isRetryDeferred,
                $consecutiveFailureCount,
                $retryAfter,
                $failureRecoveryCompleted,
                $summary
            );
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$runId", result.RunId.ToString());
        command.Parameters.AddWithValue(
            "$transactionId",
            result.TransactionId is { } transactionId ? transactionId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$occurredAt", result.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$processName", (object?)result.ActiveApp?.ProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$windowTitle", (object?)result.ActiveApp?.WindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileId", (object?)result.MatchResult?.Profile.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileName", (object?)result.MatchResult?.Profile.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$powerPlanGuid", (object?)result.ActivePowerPlan?.SchemeGuid.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$powerPlanName", (object?)result.ActivePowerPlan?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", (object?)result.PriorityChange?.AppliedPriority?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$success", result.Success ? 1 : 0);
        command.Parameters.AddWithValue("$isRetryDeferred", result.IsRetryDeferred ? 1 : 0);
        command.Parameters.AddWithValue("$consecutiveFailureCount", result.ConsecutiveFailureCount);
        command.Parameters.AddWithValue(
            "$retryAfter",
            result.RetryAfter is { } retryAfter ? retryAfter.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue(
            "$failureRecoveryCompleted",
            result.FailureRecoveryCompleted is { } recoveryCompleted
                ? recoveryCompleted ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue("$summary", result.Summary);

        var schedulerRunId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        for (var index = 0; index < result.Actions.Count; index++)
        {
            await InsertActionAsync(
                connection,
                (SqliteTransaction)transaction,
                schedulerRunId,
                index,
                result.Actions[index],
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SchedulerRunHistoryEntry>> LoadRecentRunsAsync(
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        var limitedCount = Math.Clamp(count, 1, 200);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH recent_runs AS (
                SELECT *
                FROM scheduler_runs
                ORDER BY id DESC
                LIMIT $count
            )
            SELECT
                r.id,
                r.run_id,
                r.transaction_id,
                r.occurred_at,
                r.process_name,
                r.window_title,
                r.profile_id,
                r.profile_name,
                r.power_plan_guid,
                r.power_plan_name,
                r.priority,
                r.success,
                r.is_retry_deferred,
                r.consecutive_failure_count,
                r.retry_after,
                r.failure_recovery_completed,
                r.summary,
                a.action_id,
                a.kind,
                a.target,
                a.original_value,
                a.target_value,
                a.status,
                a.reason_code,
                a.reason,
                a.message,
                a.recovery_description,
                a.requires_rollback,
                a.completed_at
            FROM recent_runs r
            LEFT JOIN scheduler_run_actions a ON a.scheduler_run_id = r.id
            ORDER BY r.id DESC, a.sequence ASC;
            """;
        command.Parameters.AddWithValue("$count", limitedCount);

        var builders = new List<HistoryEntryBuilder>();
        var builderByStorageId = new Dictionary<long, HistoryEntryBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storageId = reader.GetInt64(0);
            if (!builderByStorageId.TryGetValue(storageId, out var builder))
            {
                builder = ReadHistoryEntry(reader, storageId);
                builderByStorageId.Add(storageId, builder);
                builders.Add(builder);
            }

            if (TryReadAction(reader, builder.Entry.OccurredAt) is { } action)
            {
                builder.Actions.Add(action);
            }
        }

        return builders
            .Select(builder => builder.Entry with { Actions = builder.Actions.ToArray() })
            .ToArray();
    }

    private static HistoryEntryBuilder ReadHistoryEntry(SqliteDataReader reader, long storageId)
    {
        var occurredAtText = reader.GetString(3);
        var occurredAt = DateTimeOffset.TryParse(occurredAtText, out var parsedOccurredAt)
            ? parsedOccurredAt
            : DateTimeOffset.MinValue;
        var retryAfterText = GetNullableString(reader, 14);
        DateTimeOffset? retryAfter = DateTimeOffset.TryParse(retryAfterText, out var parsedRetryAfter)
            ? parsedRetryAfter
            : null;
        return new HistoryEntryBuilder(new SchedulerRunHistoryEntry
        {
            StorageId = storageId,
            RunId = TryParseGuid(GetNullableString(reader, 1)),
            TransactionId = TryParseGuid(GetNullableString(reader, 2)),
            OccurredAt = occurredAt,
            ProcessName = GetNullableString(reader, 4) ?? string.Empty,
            WindowTitle = GetNullableString(reader, 5) ?? string.Empty,
            ProfileId = GetNullableString(reader, 6) ?? string.Empty,
            ProfileName = GetNullableString(reader, 7) ?? string.Empty,
            PowerPlanGuid = TryParseGuid(GetNullableString(reader, 8)),
            PowerPlanName = GetNullableString(reader, 9) ?? string.Empty,
            Priority = GetNullableString(reader, 10) ?? string.Empty,
            Success = reader.GetInt64(11) != 0,
            IsRetryDeferred = reader.GetInt64(12) != 0,
            ConsecutiveFailureCount = checked((int)reader.GetInt64(13)),
            RetryAfter = retryAfter,
            FailureRecoveryCompleted = reader.IsDBNull(15)
                ? null
                : reader.GetInt64(15) != 0,
            Summary = reader.GetString(16)
        });
    }

    private static SchedulerActionResult? TryReadAction(
        SqliteDataReader reader,
        DateTimeOffset fallbackCompletedAt)
    {
        var actionIdText = GetNullableString(reader, 17);
        var kindText = GetNullableString(reader, 18);
        var statusText = GetNullableString(reader, 22);
        if (!Guid.TryParse(actionIdText, out var actionId) ||
            !Enum.TryParse<SchedulerActionKind>(kindText, ignoreCase: true, out var kind) ||
            !Enum.TryParse<SchedulerActionStatus>(statusText, ignoreCase: true, out var status))
        {
            return null;
        }

        var completedAtText = GetNullableString(reader, 28);
        var completedAt = DateTimeOffset.TryParse(completedAtText, out var parsedCompletedAt)
            ? parsedCompletedAt
            : fallbackCompletedAt;
        return new SchedulerActionResult
        {
            Plan = new SchedulerActionPlan
            {
                ActionId = actionId,
                Kind = kind,
                Target = GetNullableString(reader, 19) ?? string.Empty,
                OriginalValue = GetNullableString(reader, 20) ?? string.Empty,
                TargetValue = GetNullableString(reader, 21) ?? string.Empty,
                Reason = GetNullableString(reader, 24) ?? string.Empty,
                RecoveryDescription = GetNullableString(reader, 26) ?? string.Empty,
                RequiresRollback = !reader.IsDBNull(27) && reader.GetInt64(27) != 0
            },
            Status = status,
            ReasonCode = TryParseReasonCode(GetNullableString(reader, 23)),
            Message = GetNullableString(reader, 25) ?? string.Empty,
            CompletedAt = completedAt
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid? TryParseGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    private static SchedulerActionReasonCode TryParseReasonCode(string? value) =>
        Enum.TryParse<SchedulerActionReasonCode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SchedulerActionReasonCode.None;

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var query = connection.CreateCommand();
        query.CommandText = $"PRAGMA table_info({tableName});";
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long schedulerRunId,
        int sequence,
        SchedulerActionResult result,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO scheduler_run_actions (
                scheduler_run_id,
                sequence,
                action_id,
                kind,
                target,
                original_value,
                target_value,
                status,
                reason_code,
                reason,
                message,
                recovery_description,
                requires_rollback,
                completed_at
            )
            VALUES (
                $schedulerRunId,
                $sequence,
                $actionId,
                $kind,
                $target,
                $originalValue,
                $targetValue,
                $status,
                $reasonCode,
                $reason,
                $message,
                $recoveryDescription,
                $requiresRollback,
                $completedAt
            );
            """;

        command.Parameters.AddWithValue("$schedulerRunId", schedulerRunId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$actionId", result.Plan.ActionId.ToString());
        command.Parameters.AddWithValue("$kind", result.Plan.Kind.ToString());
        command.Parameters.AddWithValue("$target", result.Plan.Target);
        command.Parameters.AddWithValue("$originalValue", result.Plan.OriginalValue);
        command.Parameters.AddWithValue("$targetValue", result.Plan.TargetValue);
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$reasonCode", result.ReasonCode.ToString());
        command.Parameters.AddWithValue("$reason", result.Plan.Reason);
        command.Parameters.AddWithValue("$message", result.Message);
        command.Parameters.AddWithValue("$recoveryDescription", result.Plan.RecoveryDescription);
        command.Parameters.AddWithValue("$requiresRollback", result.Plan.RequiresRollback ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt", result.CompletedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        // The database path may be replaced after construction. Revalidate it
        // immediately before every connection so runtime data follows the same
        // no-reparse-point policy as the JSON stores.
        FileStoreSnapshot.EnsureSafeFilePath(_databasePath);
        return new SqliteConnection($"Data Source={_databasePath}");
    }

    private sealed class HistoryEntryBuilder(SchedulerRunHistoryEntry entry)
    {
        public SchedulerRunHistoryEntry Entry { get; } = entry;

        public List<SchedulerActionResult> Actions { get; } = new();
    }
}
