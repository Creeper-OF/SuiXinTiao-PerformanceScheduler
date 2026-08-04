using System.Collections.Concurrent;
using System.Text;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Diagnostics;

public sealed class FileAppLogger : IAppLogger
{
    private readonly string _logFilePath;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public FileAppLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "performance-scheduler.log");
    }

    public event EventHandler<LogEntry>? EntryWritten;

    public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) =>
        _entries.Reverse().Take(count).Reverse().ToArray();

    public void Trace(string message) => Write(AppLogLevel.Trace, message);

    public void Info(string message) => Write(AppLogLevel.Information, message);

    public void Warn(string message) => Write(AppLogLevel.Warning, message);

    public void Error(string message, Exception? exception = null)
    {
        var resolvedMessage = exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";

        Write(AppLogLevel.Error, resolvedMessage);
    }

    public void PublishUserEvent(
        UserEventCode eventCode,
        AppLogLevel level,
        string technicalMessage,
        params object[] arguments)
    {
        if (eventCode == UserEventCode.None)
        {
            throw new ArgumentException("A user event must have a stable event code.", nameof(eventCode));
        }

        Write(
            level,
            technicalMessage,
            LogAudience.User,
            eventCode,
            arguments.ToArray());
    }

    private void Write(
        AppLogLevel level,
        string message,
        LogAudience audience = LogAudience.Developer,
        UserEventCode eventCode = UserEventCode.None,
        IReadOnlyList<object>? arguments = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message, audience, eventCode, arguments);
        _entries.Enqueue(entry);

        while (_entries.Count > 500 && _entries.TryDequeue(out _))
        {
        }

        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{audience}] [{eventCode}] {message}{Environment.NewLine}";
        File.AppendAllText(_logFilePath, line, Encoding.UTF8);
        EntryWritten?.Invoke(this, entry);
    }
}
