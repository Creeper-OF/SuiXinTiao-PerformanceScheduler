namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed record PresentMonFrameStatistics
{
    public required int SampleCount { get; init; }

    public required double AverageFramesPerSecond { get; init; }

    public required double AverageFrameTimeMilliseconds { get; init; }

    public required double FrameTimeP95Milliseconds { get; init; }

    public required double FrameTimeP99Milliseconds { get; init; }
}
