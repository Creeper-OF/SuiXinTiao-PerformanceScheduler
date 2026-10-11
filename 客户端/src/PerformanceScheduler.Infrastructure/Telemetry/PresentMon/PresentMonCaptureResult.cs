namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed record PresentMonCaptureResult
{
    public int ExitCode { get; init; }

    public string StandardError { get; init; } = string.Empty;
}
