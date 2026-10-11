using System.IO;
using PerformanceScheduler.Core.Abstractions;

namespace PerformanceScheduler.Core.Exceptions;

/// <summary>
/// Indicates that a configuration operation failed and its store could not be
/// fully restored. Callers must stop applying new configuration until the
/// store has been repaired or replaced.
/// </summary>
public sealed class ConfigurationStoreRecoveryException : IOException, IConfigurationRecoveryFailure
{
    public ConfigurationStoreRecoveryException(
        string storeName,
        Exception operationFailure,
        IReadOnlyList<Exception> recoveryFailures)
        : base(
            CreateMessage(storeName, recoveryFailures),
            new AggregateException(new[] { operationFailure }.Concat(recoveryFailures)))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentNullException.ThrowIfNull(operationFailure);
        ArgumentNullException.ThrowIfNull(recoveryFailures);

        if (recoveryFailures.Count == 0)
        {
            throw new ArgumentException("At least one recovery failure is required.", nameof(recoveryFailures));
        }

        StoreName = storeName;
        OperationFailure = operationFailure;
        RecoveryFailures = recoveryFailures.ToArray();
    }

    public string StoreName { get; }

    public Exception OperationFailure { get; }

    public IReadOnlyList<Exception> RecoveryFailures { get; }

    public int RecoveryFailureCount => RecoveryFailures.Count;

    private static string CreateMessage(
        string storeName,
        IReadOnlyList<Exception> recoveryFailures) =>
        $"The {storeName} configuration store failed and could not be fully restored " +
        $"({recoveryFailures.Count} recovery error(s)).";
}
