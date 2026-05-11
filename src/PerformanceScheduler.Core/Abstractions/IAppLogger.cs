using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IAppLogger
{
    event EventHandler<LogEntry>? EntryWritten;

    IReadOnlyList<LogEntry> GetRecentEntries(int count = 200);

    void Trace(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}
