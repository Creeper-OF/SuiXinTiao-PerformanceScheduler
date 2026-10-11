namespace PerformanceScheduler.Core.Telemetry.Models;

/// <summary>
/// Safe operational envelope for target-experience closed-loop auto-tuning.
/// Guarantees that automated adjustments never throttle below conservative bounds
/// and never adjust faster than a rate-limited cooldown.
/// </summary>
public sealed record TargetExperienceAutoTuningEnvelope
{
    /// <summary>
    /// The lowest allowable processor maximum frequency state percentage.
    /// Prevents CPU throttling so low that the system becomes unresponsive.
    /// Default is 60%.
    /// </summary>
    public int MinProcessorMaxStatePercent { get; init; } = 60;

    /// <summary>
    /// The highest allowable processor maximum frequency state percentage.
    /// Default is 100%.
    /// </summary>
    public int MaxProcessorMaxStatePercent { get; init; } = 100;

    /// <summary>
    /// Conservative step-up size when additional performance is needed.
    /// Default is 5%.
    /// </summary>
    public int StepUpPercent { get; init; } = 5;

    /// <summary>
    /// Step-down size when cooling or power reduction is needed.
    /// Default is 5%.
    /// </summary>
    public int StepDownPercent { get; init; } = 5;

    /// <summary>
    /// Cooldown period between consecutive adjustments to prevent oscillation.
    /// Default is 10 seconds.
    /// </summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(10);

    public static TargetExperienceAutoTuningEnvelope Default { get; } = new();
}
