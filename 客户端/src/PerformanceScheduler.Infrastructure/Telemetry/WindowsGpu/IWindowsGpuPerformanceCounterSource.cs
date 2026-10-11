namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

public interface IWindowsGpuPerformanceCounterSource
{
    Task<WindowsGpuPerformanceCounterAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<GpuProcessPerformanceSnapshot> CollectProcessAsync(
        int processId,
        CancellationToken cancellationToken = default);
}
