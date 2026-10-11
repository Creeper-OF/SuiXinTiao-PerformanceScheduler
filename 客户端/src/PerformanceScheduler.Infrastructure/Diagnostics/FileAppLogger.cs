using System.Collections.Concurrent;
using System.Text;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Infrastructure.Diagnostics;

public sealed class FileAppLogger : IAppLogger
{
    private string? _logFilePath;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public FileAppLogger(string logDirectory)
    {
        try
        {
            var fullDirectoryPath = Path.GetFullPath(logDirectory);
            FileStoreSnapshot.EnsureSafeDirectoryPath(fullDirectoryPath);
            Directory.CreateDirectory(fullDirectoryPath);

            _logFilePath = Path.Combine(fullDirectoryPath, "performance-scheduler.log");
            FileStoreSnapshot.EnsureSafeFilePath(_logFilePath);
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            // Diagnostics must never prevent the application from starting.
            // Entries remain available through GetRecentEntries when disk output
            // is unavailable or the configured path is unsafe.
            _logFilePath = null;
        }
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
        var logFilePath = _logFilePath;
        if (logFilePath is not null)
        {
            try
            {
                FileStoreSnapshot.EnsureSafeFilePath(logFilePath);
                File.AppendAllText(logFilePath, line, Encoding.UTF8);
            }
            catch (Exception exception) when (IsRecoverableFileSystemException(exception))
            {
                // Keep the in-memory entry and event even when file logging is
                // temporarily unavailable due to permissions or file locks.
                _logFilePath = null;
            }
        }

        PublishEntry(entry);
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException;

    private void PublishEntry(LogEntry entry)
    {
        var handlers = EntryWritten?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.OfType<EventHandler<LogEntry>>())
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception)
            {
                // A diagnostic subscriber, especially a closing UI dispatcher,
                // must not make logging or scheduling fail.
            }
        }
    }
}
