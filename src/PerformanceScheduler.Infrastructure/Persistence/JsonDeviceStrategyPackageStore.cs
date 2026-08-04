using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonDeviceStrategyPackageStore : IDeviceStrategyPackageStore, IConfigurationLoadIssueSource
{
    private readonly string _packagesDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();

    public JsonDeviceStrategyPackageStore(string packagesDirectory)
    {
        _packagesDirectory = packagesDirectory;
        Directory.CreateDirectory(_packagesDirectory);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public IReadOnlyList<ConfigurationLoadIssue> LoadIssues => _loadIssues.ToArray();

    public async Task<IReadOnlyList<DeviceStrategyPackage>> LoadPackagesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_packagesDirectory))
        {
            return Array.Empty<DeviceStrategyPackage>();
        }

        var packages = new List<DeviceStrategyPackage>();
        _loadIssues.Clear();
        foreach (var path in Directory.GetFiles(_packagesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var package = await ReadSinglePackageAsync(path, cancellationToken);
            if (package is not null)
            {
                packages.Add(package);
            }
        }

        return packages
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SavePackageAsync(DeviceStrategyPackage package, CancellationToken cancellationToken = default)
    {
        ConfigurationSchemaPolicy.ValidateDeviceStrategyPackage(package);
        ValidatePackageProfiles(package);
        Directory.CreateDirectory(_packagesDirectory);
        var path = GetPackagePath(package);
        await AtomicFile.WriteJsonAsync(path, package, _jsonOptions, cancellationToken);

        foreach (var existingPath in Directory.GetFiles(_packagesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingPackage = await ReadSinglePackageAsync(existingPath, cancellationToken);
            if (string.Equals(existingPackage?.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(existingPath);
            }
        }
    }

    private string GetPackagePath(DeviceStrategyPackage package)
    {
        var safeId = SanitizeFileName(package.PackageId);
        var safeName = SanitizeFileName(package.Name);
        return Path.Combine(_packagesDirectory, $"{safeId}-{safeName}.json");
    }

    private async Task<DeviceStrategyPackage?> ReadSinglePackageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var package = await JsonSerializer.DeserializeAsync<DeviceStrategyPackage>(stream, _jsonOptions, cancellationToken);
            return package is null
                ? null
                : ValidateLoadedPackage(package, path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        catch (UnsupportedConfigurationSchemaException exception)
        {
            _loadIssues.Add(new ConfigurationLoadIssue
            {
                SourcePath = path,
                Message = exception.Message
            });
            return null;
        }
        catch (InvalidProfileConfigurationException exception)
        {
            _loadIssues.Add(new ConfigurationLoadIssue
            {
                SourcePath = path,
                Message = exception.Message
            });
            return null;
        }
    }

    private static DeviceStrategyPackage ValidateLoadedPackage(
        DeviceStrategyPackage package,
        string path)
    {
        ConfigurationSchemaPolicy.ValidateDeviceStrategyPackage(package, path);
        ValidatePackageProfiles(package, path);
        return package;
    }

    private static void ValidatePackageProfiles(DeviceStrategyPackage package, string? source = null)
    {
        var profiles = package.Profiles is null
            ? new PerformanceProfile?[] { null }
            : package.Profiles.Select(item => item?.Profile).ToArray();
        ProfileSafetyValidator.EnsureLibraryCanPersist(profiles, source);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(sanitized) ? "device-strategy" : sanitized.Trim();
    }
}
