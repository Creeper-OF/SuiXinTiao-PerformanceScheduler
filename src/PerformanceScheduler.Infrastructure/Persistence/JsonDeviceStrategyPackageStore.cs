using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonDeviceStrategyPackageStore : IDeviceStrategyPackageStore
{
    private readonly string _packagesDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

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

    public async Task<IReadOnlyList<DeviceStrategyPackage>> LoadPackagesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_packagesDirectory))
        {
            return Array.Empty<DeviceStrategyPackage>();
        }

        var packages = new List<DeviceStrategyPackage>();
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
        Directory.CreateDirectory(_packagesDirectory);
        var path = GetPackagePath(package);

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

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, package, _jsonOptions, cancellationToken);
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
            return await JsonSerializer.DeserializeAsync<DeviceStrategyPackage>(stream, _jsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(sanitized) ? "device-strategy" : sanitized.Trim();
    }
}
