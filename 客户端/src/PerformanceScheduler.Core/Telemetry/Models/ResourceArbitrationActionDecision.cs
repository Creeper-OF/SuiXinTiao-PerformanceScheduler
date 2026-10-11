namespace PerformanceScheduler.Core.Telemetry.Models;

public sealed record ResourceArbitrationActionDecision
{
    public bool IsEnabled { get; init; }

    public bool AllowBackgroundActions { get; init; } = true;

    public IReadOnlySet<int> AllowedProcessIds { get; init; } = new HashSet<int>();

    public IReadOnlyDictionary<int, DateTimeOffset> AllowedProcessStartTimes { get; init; } =
        new Dictionary<int, DateTimeOffset>();

    public string ReasonKey { get; init; } = "ResourceArbitration.NotEvaluated";
}
