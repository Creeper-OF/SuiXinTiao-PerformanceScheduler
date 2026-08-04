namespace PerformanceScheduler.Core.Telemetry.Models;

public static class TelemetryReasonCodes
{
    public const string TotalBudgetExceeded = "Telemetry.TotalBudgetExceeded";
    public const string CapabilityTimeout = "Telemetry.CapabilityTimeout";
    public const string CapabilityFailed = "Telemetry.CapabilityFailed";
    public const string ProviderUnsupported = "Telemetry.ProviderUnsupported";
    public const string MinimumIntervalNotElapsed = "Telemetry.MinimumIntervalNotElapsed";
    public const string CollectionTimeout = "Telemetry.CollectionTimeout";
    public const string CollectionFailed = "Telemetry.CollectionFailed";
    public const string ProcessUnavailable = "Telemetry.ProcessUnavailable";
    public const string CpuWarmup = "Telemetry.CpuWarmup";
    public const string PresentMonUnavailable = "Telemetry.PresentMonUnavailable";
    public const string PresentMonCollectorConflict = "Telemetry.PresentMonCollectorConflict";
    public const string PresentMonAccessDenied = "Telemetry.PresentMonAccessDenied";
    public const string PresentMonCaptureFailed = "Telemetry.PresentMonCaptureFailed";
    public const string PresentMonNoFrameSamples = "Telemetry.PresentMonNoFrameSamples";
    public const string PresentMonInvalidOutput = "Telemetry.PresentMonInvalidOutput";
    public const string GpuPerformanceCountersUnavailable = "Telemetry.GpuPerformanceCountersUnavailable";
    public const string GpuProcessNoSamples = "Telemetry.GpuProcessNoSamples";
    public const string GpuPerformanceCounterQueryFailed = "Telemetry.GpuPerformanceCounterQueryFailed";
    public const string SystemPerformanceCountersUnavailable = "Telemetry.SystemPerformanceCountersUnavailable";
    public const string SystemPerformanceCounterNoSamples = "Telemetry.SystemPerformanceCounterNoSamples";
    public const string SystemPerformanceCounterQueryFailed = "Telemetry.SystemPerformanceCounterQueryFailed";
}
