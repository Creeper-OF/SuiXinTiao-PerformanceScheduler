using System.IO;
using PerformanceScheduler.Core.Abstractions;

namespace PerformanceScheduler.App.Services;

/// <summary>
/// Indicates that a migration package failed while being applied.
/// The import service attempts to restore every participating store before throwing
/// this exception; callers can inspect whether that restoration completed.
/// </summary>
public sealed class MigrationPackageImportException : IOException, IConfigurationRecoveryFailure
{
    public MigrationPackageImportException(
        Exception importFailure,
        IReadOnlyList<MigrationPackageRollbackFailure> rollbackFailures)
        : base(CreateMessage(importFailure, rollbackFailures), importFailure)
    {
        ImportFailure = importFailure;
        RollbackFailures = rollbackFailures;
    }

    public Exception ImportFailure { get; }

    public IReadOnlyList<MigrationPackageRollbackFailure> RollbackFailures { get; }

    public bool RollbackSucceeded => RollbackFailures.Count == 0;

    public int RecoveryFailureCount => RollbackFailures.Count;

    private static string CreateMessage(
        Exception importFailure,
        IReadOnlyList<MigrationPackageRollbackFailure> rollbackFailures) =>
        rollbackFailures.Count == 0
            ? "The migration package was not applied; all changed stores were restored."
            : $"The migration package failed and restoration was incomplete ({rollbackFailures.Count} store error(s)).";
}

public sealed record MigrationPackageRollbackFailure(
    string StoreName,
    Exception Exception);
