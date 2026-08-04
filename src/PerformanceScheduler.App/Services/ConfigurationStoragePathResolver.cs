using System.IO;

namespace PerformanceScheduler.App.Services;

internal sealed record ConfigurationStoragePaths(
    string ProfilesDirectory,
    string DeviceStrategiesDirectory,
    bool IsPortable);

internal static class ConfigurationStoragePathResolver
{
    public static ConfigurationStoragePaths Resolve(string applicationDirectory, string applicationDataRoot)
    {
        if (CanWriteToDirectory(applicationDirectory))
        {
            return new ConfigurationStoragePaths(
                Path.Combine(applicationDirectory, "profiles"),
                Path.Combine(applicationDirectory, "device-strategies"),
                IsPortable: true);
        }

        var configurationRoot = Path.Combine(applicationDataRoot, "configuration");
        var profilesDirectory = Path.Combine(configurationRoot, "profiles");
        var deviceStrategiesDirectory = Path.Combine(configurationRoot, "device-strategies");
        Directory.CreateDirectory(profilesDirectory);
        Directory.CreateDirectory(deviceStrategiesDirectory);

        SeedJsonFiles(Path.Combine(applicationDirectory, "profiles"), profilesDirectory);
        SeedJsonFiles(Path.Combine(applicationDirectory, "device-strategies"), deviceStrategiesDirectory);

        return new ConfigurationStoragePaths(
            profilesDirectory,
            deviceStrategiesDirectory,
            IsPortable: false);
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            using (new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SeedJsonFiles(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory) ||
            Directory.EnumerateFiles(destinationDirectory, "*.json", SearchOption.TopDirectoryOnly).Any())
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }
}
