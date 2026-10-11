namespace PerformanceScheduler.Core.Models;

public sealed record DeviceStrategyPackageEvaluation
{
    public required DeviceStrategyPackage Package { get; init; }

    public required DeviceCompatibilityResult Compatibility { get; init; }

    public int EnabledProfileCount { get; init; }

    public int PowerSourceProfileCount { get; init; }

    public bool IsEligible =>
        Package.IsEnabled &&
        (Compatibility.Level is DeviceCompatibilityLevel.SameHardware or DeviceCompatibilityLevel.SameMachineModel) &&
        PowerSourceProfileCount > 0;

    public string RejectionReasonKey { get; init; } = string.Empty;
}
