namespace PerformanceScheduler.Core.Models;

public sealed record DeviceStrategyRuntimeSelection
{
    public IReadOnlyList<PerformanceProfile> Profiles { get; init; } = Array.Empty<PerformanceProfile>();

    public PowerSourceMode PowerSourceMode { get; init; } = PowerSourceMode.Any;

    public DeviceStrategyPackage? SelectedPackage { get; init; }

    public DeviceCompatibilityResult? SelectedCompatibility { get; init; }

    public IReadOnlySet<string> PackageProfileIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public int OverlappingProfileCount { get; init; }

    public IReadOnlyList<DeviceStrategyPackageEvaluation> Evaluations { get; init; } =
        Array.Empty<DeviceStrategyPackageEvaluation>();

    public string SelectionReasonKey { get; init; } = "DeviceStrategyRuntime.LocalProfilesOnly";

    public bool UsedDeviceStrategyPackage => SelectedPackage is not null;

    public static DeviceStrategyRuntimeSelection LocalOnly(
        IReadOnlyList<PerformanceProfile> localProfiles,
        PowerSourceMode powerSourceMode,
        string reasonKey = "DeviceStrategyRuntime.LocalProfilesOnly") =>
        new()
        {
            Profiles = localProfiles,
            PowerSourceMode = powerSourceMode,
            SelectionReasonKey = reasonKey
        };
}
