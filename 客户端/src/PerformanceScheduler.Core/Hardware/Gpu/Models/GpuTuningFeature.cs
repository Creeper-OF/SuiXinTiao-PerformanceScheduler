namespace PerformanceScheduler.Core.Models;

public enum GpuTuningFeature
{
    Telemetry,
    ResetToDefault,
    PowerLimit,
    CoreClockLimit,
    CoreClockOffset,
    MemoryClockLimit,
    MemoryClockOffset,
    VoltageOffset,
    VoltageCurve,
    TemperatureLimit,
    FanControl
}
