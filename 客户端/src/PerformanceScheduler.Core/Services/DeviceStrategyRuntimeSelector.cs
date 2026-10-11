using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class DeviceStrategyRuntimeSelector
{
    private readonly DeviceCompatibilityEvaluator _compatibilityEvaluator;

    public DeviceStrategyRuntimeSelector(DeviceCompatibilityEvaluator? compatibilityEvaluator = null)
    {
        _compatibilityEvaluator = compatibilityEvaluator ?? new DeviceCompatibilityEvaluator();
    }

    public DeviceStrategyRuntimeSelection Select(
        DeviceFingerprint currentDevice,
        PowerSourceMode powerSourceMode,
        IEnumerable<DeviceStrategyPackage> packages,
        IEnumerable<PerformanceProfile> localProfiles)
    {
        ArgumentNullException.ThrowIfNull(currentDevice);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(localProfiles);

        var local = localProfiles.ToArray();
        var evaluations = packages
            .Where(static package => package is not null)
            .GroupBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(package => package.Version)
                .ThenByDescending(package => package.UpdatedAt)
                .ThenBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .Select(package => EvaluatePackage(package, currentDevice, powerSourceMode))
            .ToArray();

        var selected = evaluations
            .Where(static evaluation => evaluation.IsEligible)
            .OrderByDescending(evaluation => GetCompatibilityRank(evaluation.Compatibility.Level))
            .ThenByDescending(evaluation => evaluation.PowerSourceProfileCount)
            .ThenByDescending(evaluation => evaluation.Package.Version)
            .ThenByDescending(evaluation => evaluation.Package.UpdatedAt)
            .ThenBy(evaluation => evaluation.Package.PackageId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (selected is null)
        {
            return DeviceStrategyRuntimeSelection.LocalOnly(
                local,
                powerSourceMode,
                GetFallbackReasonKey(evaluations));
        }

        var localIds = local
            .Where(static profile => !string.IsNullOrWhiteSpace(profile.Id))
            .Select(static profile => profile.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = local.ToList();
        var packageProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overlappingProfileCount = 0;

        foreach (var profile in GetEnabledProfiles(selected.Package))
        {
            if (!localIds.Add(profile.Id))
            {
                overlappingProfileCount++;
                continue;
            }

            merged.Add(profile);
            packageProfileIds.Add(profile.Id);
        }

        return new DeviceStrategyRuntimeSelection
        {
            Profiles = merged,
            PowerSourceMode = powerSourceMode,
            SelectedPackage = selected.Package,
            SelectedCompatibility = selected.Compatibility,
            PackageProfileIds = packageProfileIds,
            OverlappingProfileCount = overlappingProfileCount,
            Evaluations = evaluations,
            SelectionReasonKey = selected.Compatibility.Level switch
            {
                DeviceCompatibilityLevel.SameHardware => "DeviceStrategyRuntime.SelectedSameHardware",
                DeviceCompatibilityLevel.SameMachineModel => "DeviceStrategyRuntime.SelectedSameMachineModel",
                _ => "DeviceStrategyRuntime.LocalProfilesOnly"
            }
        };
    }

    private DeviceStrategyPackageEvaluation EvaluatePackage(
        DeviceStrategyPackage package,
        DeviceFingerprint currentDevice,
        PowerSourceMode powerSourceMode)
    {
        var compatibility = _compatibilityEvaluator.Evaluate(package.TargetDevice, currentDevice);
        var enabledProfiles = GetEnabledProfiles(package).ToArray();
        var powerSourceProfileCount = enabledProfiles.Count(profile => MatchesPowerSource(profile, powerSourceMode));

        return new DeviceStrategyPackageEvaluation
        {
            Package = package,
            Compatibility = compatibility,
            EnabledProfileCount = enabledProfiles.Length,
            PowerSourceProfileCount = powerSourceProfileCount,
            RejectionReasonKey = compatibility.Level switch
            {
                _ when !package.IsEnabled => "DeviceStrategyRuntime.RejectedDisabledPackage",
                DeviceCompatibilityLevel.Unknown => "DeviceStrategyRuntime.RejectedUnknownDevice",
                DeviceCompatibilityLevel.DifferentDevice => "DeviceStrategyRuntime.RejectedDifferentDevice",
                _ when enabledProfiles.Length == 0 => "DeviceStrategyRuntime.RejectedNoEnabledProfiles",
                _ when powerSourceProfileCount == 0 => "DeviceStrategyRuntime.RejectedPowerSource",
                _ => string.Empty
            }
        };
    }

    private static IEnumerable<PerformanceProfile> GetEnabledProfiles(DeviceStrategyPackage package) =>
        package.Profiles
            .Where(static item => item is not null && item.IsEnabledByDefault && item.Profile.IsEnabled)
            .GroupBy(item => item.Profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => MaterializeRole(group.First()));

    private static PerformanceProfile MaterializeRole(DeviceStrategyProfileItem item)
    {
        var profile = item.Profile;
        return item.Role switch
        {
            DeviceStrategyProfileRole.GlobalDefault when !profile.IsGlobalDefault =>
                profile with { IsGlobalDefault = true },
            DeviceStrategyProfileRole.BatteryDefault when profile.PowerSourceMode == PowerSourceMode.Any =>
                profile with { PowerSourceMode = PowerSourceMode.Battery },
            DeviceStrategyProfileRole.PluggedInDefault when profile.PowerSourceMode == PowerSourceMode.Any =>
                profile with { PowerSourceMode = PowerSourceMode.Ac },
            _ => profile
        };
    }

    private static bool MatchesPowerSource(PerformanceProfile profile, PowerSourceMode powerSourceMode) =>
        profile.PowerSourceMode == PowerSourceMode.Any ||
        profile.PowerSourceMode == powerSourceMode;

    private static int GetCompatibilityRank(DeviceCompatibilityLevel level) => level switch
    {
        DeviceCompatibilityLevel.SameHardware => 2,
        DeviceCompatibilityLevel.SameMachineModel => 1,
        _ => 0
    };

    private static string GetFallbackReasonKey(IReadOnlyList<DeviceStrategyPackageEvaluation> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return "DeviceStrategyRuntime.NoPackages";
        }

        if (evaluations.All(static evaluation => !evaluation.Package.IsEnabled))
        {
            return "DeviceStrategyRuntime.NoEnabledPackage";
        }

        if (evaluations.All(static evaluation => evaluation.Compatibility.Level is
                DeviceCompatibilityLevel.Unknown or DeviceCompatibilityLevel.DifferentDevice))
        {
            return "DeviceStrategyRuntime.NoCompatiblePackage";
        }

        if (evaluations.All(static evaluation => evaluation.PowerSourceProfileCount == 0))
        {
            return "DeviceStrategyRuntime.NoPowerSourceProfile";
        }

        return "DeviceStrategyRuntime.NoEligiblePackage";
    }
}
