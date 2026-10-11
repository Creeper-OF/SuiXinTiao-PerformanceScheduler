namespace PerformanceScheduler.Core.Models;

public sealed record ConfigurationLoadIssue
{
    public string SourcePath { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
