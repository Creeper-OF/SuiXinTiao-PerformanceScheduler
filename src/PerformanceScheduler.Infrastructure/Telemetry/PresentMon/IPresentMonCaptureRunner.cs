namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public interface IPresentMonCaptureRunner
{
    Task<PresentMonCaptureResult> CaptureAsync(
        string executablePath,
        PresentMonCaptureRequest request,
        CancellationToken cancellationToken = default);
}
