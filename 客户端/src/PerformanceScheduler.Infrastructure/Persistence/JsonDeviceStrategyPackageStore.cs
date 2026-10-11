using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.ExceptionServices;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonDeviceStrategyPackageStore : IDeviceStrategyPackageStore, IConfigurationLoadIssueSource
{
    private readonly string _packagesDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _loadIssuesSync = new();
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();

    public JsonDeviceStrategyPackageStore(string packagesDirectory)
    {
        _packagesDirectory = Path.GetFullPath(packagesDirectory);
        FileStoreSnapshot.EnsureDirectoryStorePath(_packagesDirectory);
        Directory.CreateDirectory(_packagesDirectory);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public IReadOnlyList<ConfigurationLoadIssue> LoadIssues
    {
        get
        {
            lock (_loadIssuesSync)
            {
                return _loadIssues.ToArray();
            }
        }
    }

    public Task<ConfigurationStoreSnapshot> CaptureImportSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => CaptureImportSnapshotCoreAsync(cancellationToken),
            cancellationToken);

    public Task RestoreImportSnapshotAsync(
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => RestoreImportSnapshotCoreAsync(snapshot, cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<DeviceStrategyPackage>> LoadPackagesAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => LoadPackagesCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<IReadOnlyList<DeviceStrategyPackage>> LoadPackagesCoreAsync(
        CancellationToken cancellationToken)
    {
        ClearLoadIssues();
        if (!Directory.Exists(_packagesDirectory))
        {
            return Array.Empty<DeviceStrategyPackage>();
        }

        var packages = new List<DeviceStrategyPackage>();
        IReadOnlyList<string> packagePaths;
        try
        {
            packagePaths = FileStoreSnapshot.EnumerateTopLevelJsonFiles(_packagesDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = _packagesDirectory,
                Message = $"The device strategy store could not be enumerated safely: {exception.Message}"
            });
            return Array.Empty<DeviceStrategyPackage>();
        }

        foreach (var path in packagePaths)
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

    public async Task SavePackageAsync(
        DeviceStrategyPackage package,
        CancellationToken cancellationToken = default)
    {
        ConfigurationSchemaPolicy.ValidateDeviceStrategyPackage(package);
        ValidatePackageProfiles(package);

        await RunExclusiveAsync(
            () => SavePackageWithSnapshotCoreAsync(package, cancellationToken),
            cancellationToken);
    }

    private async Task SavePackageWithSnapshotCoreAsync(
        DeviceStrategyPackage package,
        CancellationToken cancellationToken)
    {
        await EnsureStoreReadableForWriteCoreAsync(cancellationToken);
        var snapshot = await CaptureImportSnapshotCoreAsync(cancellationToken);
        try
        {
            await SavePackageCoreAsync(package, cancellationToken);
        }
        catch (Exception saveFailure)
        {
            try
            {
                await RestoreImportSnapshotCoreAsync(snapshot, CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                throw new ConfigurationStoreRecoveryException(
                    "device-strategy-packages",
                    saveFailure,
                    [rollbackFailure]);
            }

            ExceptionDispatchInfo.Capture(saveFailure).Throw();
            throw;
        }
    }

    private async Task SavePackageCoreAsync(
        DeviceStrategyPackage package,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_packagesDirectory);
        var path = GetPackagePath(package);
        await AtomicFile.WriteJsonAsync(path, package, _jsonOptions, cancellationToken);

        foreach (var existingPath in FileStoreSnapshot.EnumerateTopLevelJsonFiles(_packagesDirectory, cancellationToken))
        {
            if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingPackage = await ReadSinglePackageAsync(existingPath, cancellationToken);
            if (string.Equals(existingPackage?.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                // Re-check after reading and immediately before deletion; the
                // file may have been replaced during the enumeration window.
                FileStoreSnapshot.EnsureSafeFilePath(existingPath);
                File.Delete(existingPath);
            }
        }

        var loadIssues = GetLoadIssuesSnapshot();
        if (loadIssues.Length > 0)
        {
            throw new InvalidDataException(
                $"The device strategy store changed during save and contains unreadable file(s): {string.Join(" | ", loadIssues.Select(issue => issue.SourcePath))}");
        }
    }

    private string GetPackagePath(DeviceStrategyPackage package)
    {
        var safeId = SanitizeFileName(package.PackageId);
        var safeName = SanitizeFileName(package.Name);
        return Path.Combine(_packagesDirectory, $"{safeId}-{safeName}.json");
    }

    private async Task EnsureStoreReadableForWriteCoreAsync(CancellationToken cancellationToken)
    {
        await LoadPackagesCoreAsync(cancellationToken);
        var loadIssues = GetLoadIssuesSnapshot();
        if (loadIssues.Length > 0)
        {
            throw new InvalidDataException(
                $"The device strategy store contains unreadable file(s): {string.Join(" | ", loadIssues.Select(issue => issue.SourcePath))}");
        }
    }

    private async Task<DeviceStrategyPackage?> ReadSinglePackageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // Re-validate immediately before opening to close the enumeration
            // to read race for user-writable configuration files.
            FileStoreSnapshot.EnsureSafeFilePath(path);
            await using var stream = File.OpenRead(path);
            var package = await JsonSerializer.DeserializeAsync<DeviceStrategyPackage>(stream, _jsonOptions, cancellationToken);
            if (package is null)
            {
                throw new JsonException("The device strategy package JSON value cannot be null.");
            }

            return ValidateLoadedPackage(package, path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = path,
                Message = $"The device strategy package could not be loaded safely: {exception.Message}"
            });
            return null;
        }
        catch (UnsupportedConfigurationSchemaException exception)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = path,
                Message = exception.Message
            });
            return null;
        }
        catch (InvalidProfileConfigurationException exception)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = path,
                Message = exception.Message
            });
            return null;
        }
        catch (ArgumentException exception)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = path,
                Message = $"The device strategy package shape is invalid: {exception.Message}"
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

    private Task<ConfigurationStoreSnapshot> CaptureImportSnapshotCoreAsync(
        CancellationToken cancellationToken) =>
        FileStoreSnapshot.CaptureDirectoryAsync(_packagesDirectory, cancellationToken);

    private async Task RestoreImportSnapshotCoreAsync(
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await FileStoreSnapshot.RestoreDirectoryAsync(_packagesDirectory, snapshot, cancellationToken);
        ClearLoadIssues();
    }

    private async Task<T> RunExclusiveAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RunExclusiveAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ClearLoadIssues()
    {
        lock (_loadIssuesSync)
        {
            _loadIssues.Clear();
        }
    }

    private void AddLoadIssue(ConfigurationLoadIssue issue)
    {
        lock (_loadIssuesSync)
        {
            _loadIssues.Add(issue);
        }
    }

    private ConfigurationLoadIssue[] GetLoadIssuesSnapshot()
    {
        lock (_loadIssuesSync)
        {
            return _loadIssues.ToArray();
        }
    }
}
