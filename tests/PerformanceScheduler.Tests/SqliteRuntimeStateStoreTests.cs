using Microsoft.Data.Sqlite;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class SqliteRuntimeStateStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _databasePath;

    public SqliteRuntimeStateStoreTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "PerformanceScheduler.Tests",
            Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_tempDirectory, "scheduler.db");
    }

    [Fact]
    public async Task RecordRunAsync_PersistsActionAuditWithParentRun()
    {
        var store = new SqliteRuntimeStateStore(_databasePath);
        await store.InitializeAsync();
        var actionId = Guid.NewGuid();
        var retryAfter = new DateTimeOffset(2026, 7, 20, 20, 30, 0, TimeSpan.FromHours(8));

        var transactionId = Guid.NewGuid();
        var run = new SchedulerRunResult
        {
            TransactionId = transactionId,
            Success = false,
            IsRetryDeferred = true,
            ConsecutiveFailureCount = 3,
            RetryAfter = retryAfter,
            FailureRecoveryCompleted = true,
            Summary = "Profile action failed.",
            Actions =
            [
                new SchedulerActionResult
                {
                    Plan = new SchedulerActionPlan
                    {
                        ActionId = actionId,
                        Kind = SchedulerActionKind.ForegroundPriority,
                        Target = "game (42)",
                        OriginalValue = "Normal",
                        TargetValue = "AboveNormal",
                        Reason = "Matched profile.",
                        RecoveryDescription = "Restore Normal.",
                        RequiresRollback = true
                    },
                    Status = SchedulerActionStatus.Failed,
                    ReasonCode = SchedulerActionReasonCode.ProviderApplyFailed,
                    Message = "Priority provider rejected the update."
                }
            ]
        };
        await store.RecordRunAsync(run);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*),
                r.run_id,
                r.transaction_id,
                r.is_retry_deferred,
                r.consecutive_failure_count,
                r.retry_after,
                r.failure_recovery_completed,
                a.action_id,
                a.kind,
                a.status,
                a.reason_code,
                a.requires_rollback
            FROM scheduler_run_actions a
            INNER JOIN scheduler_runs r ON r.id = a.scheduler_run_id;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(run.RunId.ToString(), reader.GetString(1));
        Assert.Equal(transactionId.ToString(), reader.GetString(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal(3, reader.GetInt64(4));
        Assert.Equal(retryAfter.ToString("O"), reader.GetString(5));
        Assert.Equal(1, reader.GetInt64(6));
        Assert.Equal(actionId.ToString(), reader.GetString(7));
        Assert.Equal(nameof(SchedulerActionKind.ForegroundPriority), reader.GetString(8));
        Assert.Equal(nameof(SchedulerActionStatus.Failed), reader.GetString(9));
        Assert.Equal(nameof(SchedulerActionReasonCode.ProviderApplyFailed), reader.GetString(10));
        Assert.Equal(1, reader.GetInt64(11));

        var history = await store.LoadRecentRunsAsync();
        var historyRun = Assert.Single(history);
        Assert.Equal(run.RunId, historyRun.RunId);
        Assert.Equal(transactionId, historyRun.TransactionId);
        Assert.Equal("Profile action failed.", historyRun.Summary);
        Assert.True(historyRun.IsRetryDeferred);
        Assert.Equal(3, historyRun.ConsecutiveFailureCount);
        Assert.Equal(retryAfter, historyRun.RetryAfter);
        Assert.True(historyRun.FailureRecoveryCompleted);
        var historyAction = Assert.Single(historyRun.Actions);
        Assert.Equal(actionId, historyAction.Plan.ActionId);
        Assert.Equal(SchedulerActionKind.ForegroundPriority, historyAction.Plan.Kind);
        Assert.Equal("Normal", historyAction.Plan.OriginalValue);
        Assert.Equal("AboveNormal", historyAction.Plan.TargetValue);
        Assert.True(historyAction.Plan.RequiresRollback);
        Assert.Equal(SchedulerActionStatus.Failed, historyAction.Status);
        Assert.Equal(SchedulerActionReasonCode.ProviderApplyFailed, historyAction.ReasonCode);
    }

    [Fact]
    public async Task InitializeAsync_AddsAuditColumnsToLegacyTables()
    {
        Directory.CreateDirectory(_tempDirectory);
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE scheduler_runs (
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

                CREATE TABLE scheduler_run_actions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    scheduler_run_id INTEGER NOT NULL,
                    sequence INTEGER NOT NULL,
                    action_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    target TEXT NOT NULL,
                    original_value TEXT NOT NULL,
                    target_value TEXT NOT NULL,
                    status TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    message TEXT NOT NULL,
                    recovery_description TEXT NOT NULL,
                    requires_rollback INTEGER NOT NULL,
                    completed_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteRuntimeStateStore(_databasePath);
        await store.InitializeAsync();

        await using var verificationConnection = new SqliteConnection($"Data Source={_databasePath}");
        await verificationConnection.OpenAsync();
        var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "PRAGMA table_info(scheduler_run_actions);";
        await using var reader = await verificationCommand.ExecuteReaderAsync();
        var columnNames = new List<string>();
        while (await reader.ReadAsync())
        {
            columnNames.Add(reader.GetString(1));
        }

        Assert.Contains("reason_code", columnNames, StringComparer.OrdinalIgnoreCase);
        await reader.DisposeAsync();

        var runVerificationCommand = verificationConnection.CreateCommand();
        runVerificationCommand.CommandText = "PRAGMA table_info(scheduler_runs);";
        await using var runReader = await runVerificationCommand.ExecuteReaderAsync();
        var runColumnNames = new List<string>();
        while (await runReader.ReadAsync())
        {
            runColumnNames.Add(runReader.GetString(1));
        }

        Assert.Contains("is_retry_deferred", runColumnNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("consecutive_failure_count", runColumnNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("retry_after", runColumnNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("failure_recovery_completed", runColumnNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRecentRunsAsync_ReturnsNewestRunsFirstAndRespectsCount()
    {
        var store = new SqliteRuntimeStateStore(_databasePath);
        await store.InitializeAsync();
        await store.RecordRunAsync(new SchedulerRunResult { Summary = "first", Success = true });
        await store.RecordRunAsync(new SchedulerRunResult { Summary = "second", Success = false });
        await store.RecordRunAsync(new SchedulerRunResult { Summary = "third", Success = true });

        var history = await store.LoadRecentRunsAsync(2);

        Assert.Equal(2, history.Count);
        Assert.Equal("third", history[0].Summary);
        Assert.Equal("second", history[1].Summary);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.DeleteWithRetry(_tempDirectory);
    }
}
