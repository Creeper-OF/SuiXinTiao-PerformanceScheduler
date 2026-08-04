namespace PerformanceScheduler.Core.Models;

public sealed record ProfileMatchReason
{
    public required ProfileMatchReasonKind Kind { get; init; }

    public required int ScoreContribution { get; init; }

    public string RuleValue { get; init; } = string.Empty;

    public string ActualValue { get; init; } = string.Empty;
}
