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
        ArgumentNullException.ThrowIfNull(profile);
        EnsureProfileShape(profile, source);
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
        ArgumentNullException.ThrowIfNull(package);
        EnsureSupported(
            "device strategy package",
            package.SchemaVersion,
            DeviceStrategyPackageVersion,
            source);

        if (package.Profiles is null)
        {
            throw new ArgumentException(
                FormatConfigurationMessage("Every device strategy package entry requires a profile.", source),
                nameof(package));
        }

        foreach (var item in package.Profiles)
        {
            if (item is null)
            {
                throw new ArgumentException(
                    FormatConfigurationMessage("Every device strategy package entry requires a profile.", source),
                    nameof(package));
            }

            if (item.Profile is null)
            {
                throw new ArgumentException(
                    FormatConfigurationMessage("Every device strategy package entry requires a profile.", source),
                    nameof(package));
            }

            ValidateProfile(item.Profile, source);
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
        ArgumentNullException.ThrowIfNull(entry);
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
        ArgumentNullException.ThrowIfNull(entry);
        EnsureSupported(
            "community device strategy entry",
            entry.SchemaVersion,
            CommunityDeviceStrategyEntryVersion,
            source);
        ValidateDeviceStrategyPackage(entry.Package, source);
        return entry;
    }

    private static void EnsureProfileShape(PerformanceProfile profile, string? source)
    {
        if (profile.Match is null)
        {
            throw new ArgumentException(
                FormatConfigurationMessage("Profile match settings are required.", source),
                nameof(profile));
        }

        if (profile.Match.ExecutableNames is null ||
            profile.Match.ProcessNames is null ||
            profile.Match.WindowTitleContains is null ||
            profile.Match.Classifications is null)
        {
            throw new ArgumentException(
                FormatConfigurationMessage("Profile match collections cannot be null.", source),
                nameof(profile));
        }

        if (profile.BackgroundPolicies is null)
        {
            throw new ArgumentException(
                FormatConfigurationMessage("Profile background policies cannot be null.", source),
                nameof(profile));
        }
    }

    private static string FormatConfigurationMessage(string message, string? source) =>
        string.IsNullOrWhiteSpace(source) ? message : $"{message} Source: {source}";

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
