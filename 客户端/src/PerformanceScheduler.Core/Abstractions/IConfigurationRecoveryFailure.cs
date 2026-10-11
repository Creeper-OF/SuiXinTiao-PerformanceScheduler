namespace PerformanceScheduler.Core.Abstractions;

/// <summary>
/// Marks an exception raised after a configuration store could not be fully
/// restored to its pre-operation snapshot.
/// </summary>
public interface IConfigurationRecoveryFailure
{
    int RecoveryFailureCount { get; }
}
