namespace PerformanceScheduler.Core.Models;

public sealed record DeviceCompatibilityResult
{
    public DeviceCompatibilityLevel Level { get; init; } = DeviceCompatibilityLevel.Unknown;

    public bool ShouldWarn => Level is DeviceCompatibilityLevel.Unknown or DeviceCompatibilityLevel.DifferentDevice;

    public string ReasonKey { get; init; } = "DeviceCompatibility.Unknown";
}
