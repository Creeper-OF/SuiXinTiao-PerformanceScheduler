namespace PerformanceScheduler.Core.Models;

public sealed record ProfileMatchCriteria
{
    public IReadOnlyList<string> ExecutableNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WindowTitleContains { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ProcessClassification> Classifications { get; init; } = Array.Empty<ProcessClassification>();
}
