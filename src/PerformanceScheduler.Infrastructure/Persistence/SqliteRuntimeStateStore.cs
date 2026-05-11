using Microsoft.Data.Sqlite;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class SqliteRuntimeStateStore : IRuntimeStateStore
{
    private readonly string _databasePath;

    public SqliteRuntimeStateStore(string databasePath)
    {
        _databasePath = databasePath;
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
                summary TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO scheduler_runs (
                occurred_at,
                process_name,
                window_title,
                profile_id,
                profile_name,
                power_plan_guid,
                power_plan_name,
                priority,
                success,
                summary
            )
            VALUES (
                $occurredAt,
                $processName,
                $windowTitle,
                $profileId,
                $profileName,
                $powerPlanGuid,
                $powerPlanName,
                $priority,
                $success,
                $summary
            );
            """;

        command.Parameters.AddWithValue("$occurredAt", result.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$processName", (object?)result.ActiveApp?.ProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$windowTitle", (object?)result.ActiveApp?.WindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileId", (object?)result.MatchResult?.Profile.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileName", (object?)result.MatchResult?.Profile.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$powerPlanGuid", (object?)result.ActivePowerPlan?.SchemeGuid.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$powerPlanName", (object?)result.ActivePowerPlan?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", (object?)result.PriorityChange?.AppliedPriority?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$success", result.Success ? 1 : 0);
        command.Parameters.AddWithValue("$summary", result.Summary);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection() => new($"Data Source={_databasePath}");
}
