namespace PerformanceScheduler.App.Services.Telemetry;

public enum PresentMonConfigurationStatus
{
    Unavailable,
    Configured,
    AutoDetected,
    ConfiguredPathInvalid
}

public sealed record PresentMonConfigurationState
{
    public PresentMonConfigurationStatus Status { get; init; }

    public string ConfiguredPath { get; init; } = string.Empty;

    public string ResolvedPath { get; init; } = string.Empty;
}
