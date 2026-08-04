namespace PerformanceScheduler.Core.Telemetry.Models;

public enum TelemetryMetricKind
{
    ProcessCpuUsagePercent,
    WorkingSetMegabytes,
    PrivateMemoryMegabytes,
    ThreadCount,
    HandleCount,
    FramesPerSecondAverage,
    FrameTimeAverageMilliseconds,
    FrameTimeP95Milliseconds,
    FrameTimeP99Milliseconds,
    PresentedFrameCount,
    ProcessGpuUsagePercent,
    ProcessGpuDedicatedMemoryMegabytes,
    ProcessGpuSharedMemoryMegabytes,
    SystemCpuFrequencyMegahertz,
    ThermalZoneTemperatureCelsius
}
