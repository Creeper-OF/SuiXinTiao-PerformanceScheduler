namespace PerformanceScheduler.Core.Models;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string Message);
