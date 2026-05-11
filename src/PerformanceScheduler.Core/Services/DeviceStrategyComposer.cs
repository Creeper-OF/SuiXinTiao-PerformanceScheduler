using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class DeviceStrategyComposer
{
    public DeviceStrategyPackage AddOrReplaceProfile(
        DeviceStrategyPackage package,
        PerformanceProfile profile,
        DeviceStrategyProfileRole role = DeviceStrategyProfileRole.AppSpecific,
        DeviceStrategyProfileSource source = DeviceStrategyProfileSource.UserAdded,
        string? sourceProfileEntryId = null)
    {
        var item = new DeviceStrategyProfileItem
        {
            ItemId = Guid.NewGuid().ToString("N"),
            Role = role,
            Source = source,
            SourceProfileEntryId = sourceProfileEntryId,
            Profile = profile
        };

        var profiles = package.Profiles
            .Where(existing => !string.Equals(existing.Profile.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Append(item)
            .ToArray();

        return package with { Profiles = profiles };
    }

    public DeviceStrategyPackage RemoveProfile(DeviceStrategyPackage package, string profileId)
    {
        var profiles = package.Profiles
            .Where(item => !string.Equals(item.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return package with { Profiles = profiles };
    }

    public DeviceStrategyPackage CreateFromProfiles(
        string name,
        DeviceFingerprint targetDevice,
        IEnumerable<PerformanceProfile> profiles)
    {
        return new DeviceStrategyPackage
        {
            Name = name,
            TargetDevice = targetDevice,
            Profiles = profiles
                .Select(profile => new DeviceStrategyProfileItem
                {
                    Role = profile.IsGlobalDefault
                        ? DeviceStrategyProfileRole.GlobalDefault
                        : profile.PowerSourceMode == PowerSourceMode.Battery
                            ? DeviceStrategyProfileRole.BatteryDefault
                            : profile.PowerSourceMode == PowerSourceMode.Ac
                                ? DeviceStrategyProfileRole.PluggedInDefault
                                : DeviceStrategyProfileRole.AppSpecific,
                    Source = DeviceStrategyProfileSource.UserAdded,
                    Profile = profile
                })
                .ToArray()
        };
    }
}
