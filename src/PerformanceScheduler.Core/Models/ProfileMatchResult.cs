namespace PerformanceScheduler.Core.Models;

public sealed record ProfileMatchResult(
    PerformanceProfile Profile,
    int Score,
    string Reason);
