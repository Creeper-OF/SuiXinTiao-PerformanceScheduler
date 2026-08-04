namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed record PresentMonCaptureRequest
{
    public required int ProcessId { get; init; }

    public required TimeSpan Duration { get; init; }

    public required string OutputFilePath { get; init; }

    public required string SessionName { get; init; }
}
