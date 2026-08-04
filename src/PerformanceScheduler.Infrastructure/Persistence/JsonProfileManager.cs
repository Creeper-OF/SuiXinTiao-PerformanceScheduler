using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonProfileManager : IProfileManager, IConfigurationLoadIssueSource
{
    private readonly string _profilesDirectory;
    private readonly string _historyDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();

    public JsonProfileManager(string profilesDirectory)
    {
        _profilesDirectory = profilesDirectory;
        _historyDirectory = Path.Combine(_profilesDirectory, ".history");
        Directory.CreateDirectory(_profilesDirectory);
        Directory.CreateDirectory(_historyDirectory);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public IReadOnlyList<ConfigurationLoadIssue> LoadIssues => _loadIssues.ToArray();

    public async Task<IReadOnlyList<PerformanceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_profilesDirectory))
        {
            return Array.Empty<PerformanceProfile>();
        }

        var profiles = new List<PerformanceProfile>();
        _loadIssues.Clear();
        foreach (var path in GetProfilePaths())
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

        foreach (var existingPath in GetProfilePaths())
        {
            if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingProfile = await ReadSingleProfileAsync(existingPath, cancellationToken);
            if (existingProfile?.Id == profile.Id)
            {
                File.Delete(existingPath);
            }
        }
    }

    public async Task<IReadOnlyList<ProfileRevisionInfo>> LoadProfileHistoryAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profileHistoryDirectory = GetProfileHistoryDirectory(profileId);
        if (!Directory.Exists(profileHistoryDirectory))
        {
            return Array.Empty<ProfileRevisionInfo>();
        }

        var revisions = new List<ProfileRevisionInfo>();
        foreach (var path in Directory.GetFiles(profileHistoryDirectory, "*.json", SearchOption.TopDirectoryOnly))
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

    public async Task<PerformanceProfile?> RollbackProfileAsync(string profileId, string revisionId, CancellationToken cancellationToken = default)
    {
        var revisionPath = Path.Combine(GetProfileHistoryDirectory(profileId), $"{revisionId}.json");
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
        await SaveProfileAsync(restoredProfile, cancellationToken);
        return restoredProfile;
    }

    public async Task ExportProfilesAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadProfilesAsync(cancellationToken);
        await AtomicFile.WriteJsonAsync(destinationPath, profiles, _jsonOptions, cancellationToken);
    }

    public async Task ImportProfilesAsync(string sourcePath, bool overwriteExisting, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var importedProfiles = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<List<PerformanceProfile>>(_jsonOptions) ?? new List<PerformanceProfile>(),
            JsonValueKind.Object => document.RootElement.Deserialize<PerformanceProfile>(_jsonOptions) is { } singleProfile
                ? new List<PerformanceProfile> { singleProfile }
                : new List<PerformanceProfile>(),
            _ => new List<PerformanceProfile>()
        };

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

        foreach (var profile in profilesToImport)
        {
            var destinationPath = GetProfilePath(profile);
            if (!overwriteExisting && await FindByIdAsync(profile.Id, cancellationToken) is not null)
            {
                continue;
            }

            await SaveProfileAsync(profile, cancellationToken);
        }
    }

    private IEnumerable<string> GetProfilePaths() =>
        Directory.GetFiles(_profilesDirectory, "*.json", SearchOption.TopDirectoryOnly);

    private async Task<IReadOnlyList<PerformanceProfile>> LoadProfilesForValidationAsync(
        CancellationToken cancellationToken)
    {
        var profiles = new List<PerformanceProfile>();
        foreach (var path in GetProfilePaths())
        {
            if (await ReadSingleProfileAsync(path, cancellationToken) is { } profile)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    private async Task<(string Path, PerformanceProfile Profile)?> FindByIdAsync(string profileId, CancellationToken cancellationToken)
    {
        foreach (var path in GetProfilePaths())
        {
            var profile = await ReadSingleProfileAsync(path, cancellationToken);
            if (profile?.Id == profileId)
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
                await using var sourceStream = File.OpenRead(sourcePath);
                await sourceStream.CopyToAsync(destinationStream, token);
            },
            cancellationToken);
    }

    private async Task<PerformanceProfile?> ReadSingleProfileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<PerformanceProfile>(stream, _jsonOptions, cancellationToken);
            return profile is null
                ? null
                : ValidateLoadedProfile(profile, path);
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
        return string.IsNullOrWhiteSpace(sanitized) ? "profile" : sanitized.Trim();
    }
}
