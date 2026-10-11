using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.ExceptionServices;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonProfileManager : IProfileManager, IConfigurationLoadIssueSource
{
    private readonly string _profilesDirectory;
    private readonly string _historyDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _loadIssuesSync = new();
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();

    public JsonProfileManager(string profilesDirectory)
    {
        _profilesDirectory = Path.GetFullPath(profilesDirectory);
        _historyDirectory = Path.Combine(_profilesDirectory, ".history");
        FileStoreSnapshot.EnsureDirectoryStorePath(_profilesDirectory);
        FileStoreSnapshot.EnsureDirectoryStorePath(_historyDirectory);
        Directory.CreateDirectory(_profilesDirectory);
        Directory.CreateDirectory(_historyDirectory);
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

    public Task<IReadOnlyList<PerformanceProfile>> LoadProfilesAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => LoadProfilesCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<IReadOnlyList<PerformanceProfile>> LoadProfilesCoreAsync(
        CancellationToken cancellationToken)
    {
        ClearLoadIssues();
        if (!Directory.Exists(_profilesDirectory))
        {
            return Array.Empty<PerformanceProfile>();
        }

        var profiles = new List<PerformanceProfile>();
        IReadOnlyList<string> profilePaths;
        try
        {
            profilePaths = GetProfilePaths(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = _profilesDirectory,
                Message = $"The profile store could not be enumerated safely: {exception.Message}"
            });
            return Array.Empty<PerformanceProfile>();
        }

        foreach (var path in profilePaths)
        {
            var profile = await ReadSingleProfileAsync(path, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task SaveProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default)
    {
        ConfigurationSchemaPolicy.ValidateProfile(profile);
        ProfileSafetyValidator.EnsureCanPersist(profile);

        await RunExclusiveAsync(
            () => SaveProfileWithSnapshotCoreAsync(profile, cancellationToken),
            cancellationToken);
    }

    private async Task SaveProfileWithSnapshotCoreAsync(
        PerformanceProfile profile,
        CancellationToken cancellationToken)
    {
        var snapshot = await CaptureImportSnapshotCoreAsync(cancellationToken);
        try
        {
            await SaveProfileCoreAsync(profile, cancellationToken);
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
                    "profiles",
                    saveFailure,
                    [rollbackFailure]);
            }

            ExceptionDispatchInfo.Capture(saveFailure).Throw();
            throw;
        }
    }

    private async Task SaveProfileCoreAsync(
        PerformanceProfile profile,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_profilesDirectory);
        var path = GetProfilePath(profile);
        var existingProfiles = await LoadProfilesForValidationAsync(cancellationToken);
        ProfileSafetyValidator.EnsureLibraryCanPersist(
            existingProfiles
                .Where(existing => !string.Equals(existing.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                .Append(profile));
        var existingCurrent = await FindByIdAsync(profile.Id, cancellationToken);

        if (existingCurrent is not null)
        {
            await ArchiveRevisionAsync(existingCurrent.Value.Profile, existingCurrent.Value.Path, cancellationToken);
        }

        await AtomicFile.WriteJsonAsync(path, profile, _jsonOptions, cancellationToken);

        foreach (var existingPath in GetProfilePaths(cancellationToken))
        {
            if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingProfile = await ReadSingleProfileAsync(existingPath, cancellationToken);
            if (string.Equals(existingProfile?.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
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
                $"The profile store changed during save and contains unreadable file(s): {string.Join(" | ", loadIssues.Select(issue => issue.SourcePath))}");
        }
    }

    public Task<IReadOnlyList<ProfileRevisionInfo>> LoadProfileHistoryAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => LoadProfileHistoryCoreAsync(profileId, cancellationToken),
            cancellationToken);

    private async Task<IReadOnlyList<ProfileRevisionInfo>> LoadProfileHistoryCoreAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var profileHistoryDirectory = GetProfileHistoryDirectory(profileId);
        if (!Directory.Exists(profileHistoryDirectory))
        {
            return Array.Empty<ProfileRevisionInfo>();
        }

        var revisions = new List<ProfileRevisionInfo>();
        IReadOnlyList<string> historyPaths;
        try
        {
            historyPaths = FileStoreSnapshot.EnumerateTopLevelJsonFiles(profileHistoryDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AddLoadIssue(new ConfigurationLoadIssue
            {
                SourcePath = profileHistoryDirectory,
                Message = $"The profile history could not be enumerated safely: {exception.Message}"
            });
            return Array.Empty<ProfileRevisionInfo>();
        }

        foreach (var path in historyPaths)
        {
            var profile = await ReadSingleProfileAsync(path, cancellationToken);
            if (profile is null)
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            var createdAt = TryParseRevisionTimestamp(fileName);
            revisions.Add(new ProfileRevisionInfo
            {
                RevisionId = fileName,
                DisplayName = $"{createdAt:yyyy-MM-dd HH:mm:ss} | v{profile.Version} | {profile.Name}",
                CreatedAt = createdAt,
                Profile = profile
            });
        }

        return revisions
            .OrderByDescending(revision => revision.CreatedAt)
            .ToArray();
    }

    public Task<PerformanceProfile?> RollbackProfileAsync(
        string profileId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => RollbackProfileCoreAsync(profileId, revisionId, cancellationToken),
            cancellationToken);

    private async Task<PerformanceProfile?> RollbackProfileCoreAsync(
        string profileId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRevisionId(revisionId))
        {
            return null;
        }

        var revisionPath = Path.Combine(GetProfileHistoryDirectory(profileId), $"{revisionId}.json");
        // History directories are user-writable state. Re-validate the complete
        // path at the read boundary so a newly introduced reparse point cannot
        // redirect rollback reads outside the profile store.
        FileStoreSnapshot.EnsureSafeFilePath(revisionPath);
        if (!File.Exists(revisionPath))
        {
            return null;
        }

        var profile = await ReadSingleProfileAsync(revisionPath, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var restoredProfile = profile with { Version = Math.Max(1, profile.Version + 1) };
        await SaveProfileWithSnapshotCoreAsync(restoredProfile, cancellationToken);
        return restoredProfile;
    }

    public Task ExportProfilesAsync(
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async () =>
            {
                var profiles = await LoadProfilesCoreAsync(cancellationToken);
                await AtomicFile.WriteJsonAsync(destinationPath, profiles, _jsonOptions, cancellationToken);
            },
            cancellationToken);

    public Task ImportProfilesAsync(
        string sourcePath,
        bool overwriteExisting,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => ImportProfilesCoreAsync(sourcePath, overwriteExisting, cancellationToken),
            cancellationToken);

    private async Task ImportProfilesCoreAsync(
        string sourcePath,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        // Direct profile imports are also user-selected filesystem input.
        // Keep this boundary consistent with migration-package imports.
        FileStoreSnapshot.EnsureSafeFilePath(sourcePath);
        // Re-check immediately before reading the user-selected file.
        FileStoreSnapshot.EnsureSafeFilePath(sourcePath);
        var json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var importedProfiles = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<List<PerformanceProfile>>(_jsonOptions)
                                   ?? throw new JsonException("The profile import array is null."),
            JsonValueKind.Object => document.RootElement.Deserialize<PerformanceProfile>(_jsonOptions) is { } singleProfile
                ? new List<PerformanceProfile> { singleProfile }
                : throw new JsonException("The profile import object is null."),
            _ => throw new JsonException("A profile import must contain a JSON object or array.")
        };

        if (importedProfiles.Any(static profile => profile is null))
        {
            throw new JsonException("A profile import cannot contain a null profile entry.");
        }

        foreach (var profile in importedProfiles)
        {
            ConfigurationSchemaPolicy.ValidateProfile(profile, sourcePath);
        }

        var profilesToImport = importedProfiles
            .Select(profile => ProfileSafetyValidator.PrepareUntrustedImport(profile, sourcePath))
            .ToArray();

        ProfileSafetyValidator.EnsureLibraryCanPersist(profilesToImport, sourcePath);

        var existingProfiles = await LoadProfilesForValidationAsync(cancellationToken);
        var prospectiveProfiles = existingProfiles.ToDictionary(
            profile => profile.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profilesToImport)
        {
            if (!overwriteExisting && prospectiveProfiles.ContainsKey(profile.Id))
            {
                continue;
            }

            prospectiveProfiles[profile.Id] = profile;
        }

        ProfileSafetyValidator.EnsureLibraryCanPersist(prospectiveProfiles.Values, sourcePath);

        if (profilesToImport.Length == 0)
        {
            return;
        }

        var snapshot = await CaptureImportSnapshotCoreAsync(cancellationToken);
        try
        {
            foreach (var profile in profilesToImport)
            {
                if (!overwriteExisting && await FindByIdAsync(profile.Id, cancellationToken) is not null)
                {
                    continue;
                }

                await SaveProfileWithSnapshotCoreAsync(profile, cancellationToken);
            }
        }
        catch (Exception importFailure)
        {
            try
            {
                await RestoreImportSnapshotCoreAsync(snapshot, CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                throw new ConfigurationStoreRecoveryException(
                    "profiles",
                    importFailure,
                    [rollbackFailure]);
            }

            ExceptionDispatchInfo.Capture(importFailure).Throw();
            throw;
        }
    }

    private Task<ConfigurationStoreSnapshot> CaptureImportSnapshotCoreAsync(
        CancellationToken cancellationToken) =>
        FileStoreSnapshot.CaptureDirectoryAsync(_profilesDirectory, cancellationToken);

    private async Task RestoreImportSnapshotCoreAsync(
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await FileStoreSnapshot.RestoreDirectoryAsync(_profilesDirectory, snapshot, cancellationToken);
        ClearLoadIssues();
    }

    private IReadOnlyList<string> GetProfilePaths(CancellationToken cancellationToken = default) =>
        FileStoreSnapshot.EnumerateTopLevelJsonFiles(_profilesDirectory, cancellationToken);

    private async Task<IReadOnlyList<PerformanceProfile>> LoadProfilesForValidationAsync(
        CancellationToken cancellationToken)
    {
        ClearLoadIssues();
        var profiles = new List<PerformanceProfile>();
        foreach (var path in GetProfilePaths(cancellationToken))
        {
            if (await ReadSingleProfileAsync(path, cancellationToken) is { } profile)
            {
                profiles.Add(profile);
            }
        }

        var loadIssues = GetLoadIssuesSnapshot();
        if (loadIssues.Length > 0)
        {
            throw new InvalidDataException(
                $"The profile store contains unreadable file(s): {string.Join(" | ", loadIssues.Select(issue => issue.SourcePath))}");
        }

        return profiles;
    }

    private async Task<(string Path, PerformanceProfile Profile)?> FindByIdAsync(string profileId, CancellationToken cancellationToken)
    {
        foreach (var path in GetProfilePaths(cancellationToken))
        {
            var profile = await ReadSingleProfileAsync(path, cancellationToken);
            if (profile is not null &&
                string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return (path, profile);
            }
        }

        return null;
    }

    private string GetProfilePath(PerformanceProfile profile)
    {
        var safeId = SanitizeFileName(profile.Id);
        var safeName = SanitizeFileName(profile.Name);
        return Path.Combine(_profilesDirectory, $"{safeId}-{safeName}.json");
    }

    private string GetProfileHistoryDirectory(string profileId) =>
        Path.Combine(_historyDirectory, SanitizeFileName(profileId));

    private async Task ArchiveRevisionAsync(PerformanceProfile profile, string sourcePath, CancellationToken cancellationToken)
    {
        FileStoreSnapshot.EnsureSafeFilePath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var profileHistoryDirectory = GetProfileHistoryDirectory(profile.Id);
        Directory.CreateDirectory(profileHistoryDirectory);
        var revisionId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-v{profile.Version}";
        var destinationPath = Path.Combine(profileHistoryDirectory, $"{revisionId}.json");

        await AtomicFile.WriteAsync(
            destinationPath,
            async (destinationStream, token) =>
            {
                FileStoreSnapshot.EnsureSafeFilePath(sourcePath);
                await using var sourceStream = File.OpenRead(sourcePath);
                await sourceStream.CopyToAsync(destinationStream, token);
            },
            cancellationToken);
    }

    private async Task<PerformanceProfile?> ReadSingleProfileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // Re-validate immediately before opening: the directory enumeration
            // check and this read are separate filesystem operations.
            FileStoreSnapshot.EnsureSafeFilePath(path);
            await using var stream = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<PerformanceProfile>(stream, _jsonOptions, cancellationToken);
            if (profile is null)
            {
                throw new JsonException("The profile JSON value cannot be null.");
            }

            return ValidateLoadedProfile(profile, path);
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
                Message = $"The profile could not be loaded safely: {exception.Message}"
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
                Message = $"The profile shape is invalid: {exception.Message}"
            });
            return null;
        }
    }

    private static PerformanceProfile ValidateLoadedProfile(PerformanceProfile profile, string path)
    {
        ConfigurationSchemaPolicy.ValidateProfile(profile, path);
        ProfileSafetyValidator.EnsureCanPersist(profile, path);
        return profile;
    }

    private static DateTimeOffset TryParseRevisionTimestamp(string revisionId)
    {
        var timestamp = revisionId.Split('-', 2)[0];
        return DateTimeOffset.TryParseExact(
            timestamp,
            "yyyyMMddHHmmssfff",
            null,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        var trimmed = sanitized.Trim();
        return trimmed switch
        {
            "." or ".." => "_",
            _ when string.IsNullOrWhiteSpace(trimmed) => "profile",
            _ => trimmed
        };
    }

    private static bool IsSafeRevisionId(string revisionId) =>
        !string.IsNullOrWhiteSpace(revisionId) &&
        string.Equals(Path.GetFileName(revisionId), revisionId, StringComparison.Ordinal) &&
        !revisionId.Contains("..", StringComparison.Ordinal) &&
        revisionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

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
