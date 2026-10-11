using System.IO;

namespace PerformanceScheduler.Core.Exceptions;

public sealed class StartupRecoveryStateUnavailableException : IOException
{
    public StartupRecoveryStateUnavailableException(string stateFilePath)
        : base("Startup recovery state exists but could not be read safely.")
    {
        StateFilePath = stateFilePath;
    }

    public string StateFilePath { get; }
}
