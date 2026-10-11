using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class DeviceStrategyComposer
{
    public DeviceStrategyPackage AddOrReplaceProfile(
        DeviceStrategyPackage package,
        PerformanceProfile profile,
        DeviceStrategyProfileRole? role = null,
        DeviceStrategyProfileSource source = DeviceStrategyProfileSource.UserAdded,
        string? sourceProfileEntryId = null)
    {
        var item = new DeviceStrategyProfileItem
        {
            ItemId = Guid.NewGuid().ToString("N"),
            Role = role ?? ResolveRole(profile),
            Source = source,
            SourceProfileEntryId = sourceProfileEntryId,
            Profile = profile
        };

        var profiles = package.Profiles
            .Where(existing => !string.Equals(existing.Profile.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Append(item)
            .ToArray();

        return package with
        {
            Profiles = profiles,
            Version = Math.Max(1, package.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public DeviceStrategyPackage RemoveProfile(DeviceStrategyPackage package, string profileId)
    {
        var profiles = package.Profiles
            .Where(item => !string.Equals(item.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (profiles.Length == package.Profiles.Count)
        {
            return package;
        }

        return package with
        {
            Profiles = profiles,
            Version = Math.Max(1, package.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public DeviceStrategyPackage CreateBlank(
        string name,
        DeviceFingerprint targetDevice,
        string author = "Local",
        string summary = "")
    {
        return new DeviceStrategyPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            Name = name,
            Author = author,
            Summary = summary,
            IsEnabled = false,
            TargetDevice = targetDevice,
            Profiles = Array.Empty<DeviceStrategyProfileItem>(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public DeviceStrategyPackage Duplicate(DeviceStrategyPackage source)
    {
        return source with
        {
            PackageId = Guid.NewGuid().ToString("N"),
            Name = $"{source.Name} - 副本",
            IsEnabled = false,
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public DeviceStrategyPackage CreateFromProfiles(
        string name,
        DeviceFingerprint targetDevice,
        IEnumerable<PerformanceProfile> profiles,
        string author = "Local",
        string summary = "")
    {
        return new DeviceStrategyPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            Name = name,
            Author = author,
            Summary = summary,
            IsEnabled = false,
            TargetDevice = targetDevice,
            Profiles = profiles
                .Select(profile => new DeviceStrategyProfileItem
                {
                    Role = ResolveRole(profile),
                    Source = DeviceStrategyProfileSource.UserAdded,
                    Profile = profile
                })
                .ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static DeviceStrategyProfileRole ResolveRole(PerformanceProfile profile) =>
        profile.IsGlobalDefault
            ? DeviceStrategyProfileRole.GlobalDefault
            : profile.PowerSourceMode == PowerSourceMode.Battery
                ? DeviceStrategyProfileRole.BatteryDefault
                : profile.PowerSourceMode == PowerSourceMode.Ac
                    ? DeviceStrategyProfileRole.PluggedInDefault
                    : DeviceStrategyProfileRole.AppSpecific;
}
