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

    void PublishUserEvent(
        UserEventCode eventCode,
        AppLogLevel level,
        string technicalMessage,
        params object[] arguments)
    {
        switch (level)
        {
            case AppLogLevel.Trace:
                Trace(technicalMessage);
                break;
            case AppLogLevel.Information:
                Info(technicalMessage);
                break;
            case AppLogLevel.Warning:
                Warn(technicalMessage);
                break;
            case AppLogLevel.Error:
                Error(technicalMessage);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }
}
