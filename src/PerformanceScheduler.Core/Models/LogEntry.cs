namespace PerformanceScheduler.Core.Models;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string Message,
    LogAudience Audience = LogAudience.Developer,
    UserEventCode EventCode = UserEventCode.None,
    IReadOnlyList<object>? Arguments = null);
