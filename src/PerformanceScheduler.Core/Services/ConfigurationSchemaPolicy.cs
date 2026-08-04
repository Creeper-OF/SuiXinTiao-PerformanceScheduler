using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public static class ConfigurationSchemaPolicy
{
    public const int PerformanceProfileVersion = 1;
    public const int DeviceStrategyPackageVersion = 1;
    public const int MigrationPackageVersion = 1;
    public const int CommunityProfileEntryVersion = 1;
    public const int CommunityDeviceStrategyEntryVersion = 1;

    public static PerformanceProfile ValidateProfile(PerformanceProfile profile, string? source = null)
    {
        EnsureSupported(
            "Performance Profile",
            profile.SchemaVersion,
            PerformanceProfileVersion,
            source);
        return profile;
    }

    public static DeviceStrategyPackage ValidateDeviceStrategyPackage(
        DeviceStrategyPackage package,
        string? source = null)
    {
        EnsureSupported(
            "device strategy package",
            package.SchemaVersion,
            DeviceStrategyPackageVersion,
            source);

        foreach (var item in package.Profiles ?? Array.Empty<DeviceStrategyProfileItem>())
        {
            if (item?.Profile is { } profile)
            {
                ValidateProfile(profile, source);
            }
        }

        return package;
    }

    public static void EnsureMigrationPackageSupported(int schemaVersion, string? source = null) =>
        EnsureSupported(
            "migration package",
            schemaVersion,
            MigrationPackageVersion,
            source);

    public static CommunityProfileEntry ValidateCommunityProfileEntry(
        CommunityProfileEntry entry,
        string? source = null)
    {
        EnsureSupported(
            "community Profile entry",
            entry.SchemaVersion,
            CommunityProfileEntryVersion,
            source);
        ValidateProfile(entry.Profile, source);
        return entry;
    }

    public static CommunityDeviceStrategyEntry ValidateCommunityDeviceStrategyEntry(
        CommunityDeviceStrategyEntry entry,
        string? source = null)
    {
        EnsureSupported(
            "community device strategy entry",
            entry.SchemaVersion,
            CommunityDeviceStrategyEntryVersion,
            source);
        ValidateDeviceStrategyPackage(entry.Package, source);
        return entry;
    }

    private static void EnsureSupported(
        string documentType,
        int actualVersion,
        int supportedVersion,
        string? source)
    {
        if (actualVersion == supportedVersion)
        {
            return;
        }

        throw new UnsupportedConfigurationSchemaException(
            documentType,
            actualVersion,
            supportedVersion,
            source);
    }
}
