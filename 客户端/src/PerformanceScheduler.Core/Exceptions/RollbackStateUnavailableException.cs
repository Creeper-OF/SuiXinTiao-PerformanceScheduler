namespace PerformanceScheduler.Core.Exceptions;

public sealed class RollbackStateUnavailableException : Exception
{
    public RollbackStateUnavailableException(string stateFilePath, Exception innerException)
        : base("The rollback state exists but could not be read.", innerException)
    {
        StateFilePath = stateFilePath;
    }

    public string StateFilePath { get; }
}
