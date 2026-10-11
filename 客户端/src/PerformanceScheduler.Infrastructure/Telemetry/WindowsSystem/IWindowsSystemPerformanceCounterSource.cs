namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

public interface IWindowsSystemPerformanceCounterSource
{
    Task<WindowsSystemPerformanceCounterAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<WindowsSystemPerformanceSnapshot> CollectAsync(
        CancellationToken cancellationToken = default);
}
