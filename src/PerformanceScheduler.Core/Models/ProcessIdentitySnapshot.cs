namespace PerformanceScheduler.Core.Models;

public sealed record ProcessIdentitySnapshot
{
    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public string ExecutablePath { get; init; } = string.Empty;
}
