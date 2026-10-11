namespace PerformanceScheduler.Core.Models;

public enum TargetExperiencePriority
{
    Balanced,
    StableFrameRate,
    CoolAndQuiet,
    PowerSaving,
    Performance
}

public enum TargetExperienceControlMode
{
    ObservationOnly,
    RecommendOnly,
    Automatic
}

public enum NoisePreference
{
    Ignore,
    Quiet,
    Balanced,
    Performance
}

/// <summary>
/// User-facing experience constraints. The default is deliberately read-only;
/// an automatic controller must still pass capability checks and rollback gates.
/// </summary>
public sealed record TargetExperiencePreference
{
    public bool Enabled { get; init; }

    public int? TargetFramesPerSecond { get; init; }

    public double? MaximumFrameTimeP95Milliseconds { get; init; }

    public double? MaximumTemperatureCelsius { get; init; }

    public double? MaximumPowerWatts { get; init; }

    public TargetExperiencePriority Priority { get; init; } = TargetExperiencePriority.Balanced;

    public TargetExperienceControlMode ControlMode { get; init; } = TargetExperienceControlMode.ObservationOnly;

    public NoisePreference NoisePreference { get; init; } = NoisePreference.Ignore;

    public int ObservationWindowSeconds { get; init; } = 10;

    public int MinimumStableSamples { get; init; } = 3;

    public double HysteresisPercent { get; init; } = 5;
}
