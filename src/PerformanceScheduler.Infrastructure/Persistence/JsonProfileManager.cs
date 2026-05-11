using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonProfileManager : IProfileManager
{
    private readonly string _profilesDirectory;
    private readonly string _historyDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

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

    public async Task<IReadOnlyList<PerformanceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_profilesDirectory))
        {
            return Array.Empty<PerformanceProfile>();
        }

        var profiles = new List<PerformanceProfile>();
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
        Directory.CreateDirectory(_profilesDirectory);
        var path = GetProfilePath(profile);
        var existingCurrent = await FindByIdAsync(profile.Id, cancellationToken);

        if (existingCurrent is not null)
        {
            await ArchiveRevisionAsync(existingCurrent.Value.Profile, existingCurrent.Value.Path, cancellationToken);
        }

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

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profile, _jsonOptions, cancellationToken);
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
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, profiles, _jsonOptions, cancellationToken);
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

        await using var sourceStream = File.OpenRead(sourcePath);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    private async Task<PerformanceProfile?> ReadSingleProfileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PerformanceProfile>(stream, _jsonOptions, cancellationToken);
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
