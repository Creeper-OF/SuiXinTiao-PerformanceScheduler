using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonCommunityProfileCatalog : ICommunityProfileCatalog, IConfigurationLoadIssueSource
{
    private readonly string _seedCatalogPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();

    public JsonCommunityProfileCatalog(string storagePath, string seedCatalogPath)
    {
        StoragePath = Path.GetFullPath(storagePath);
        _seedCatalogPath = Path.GetFullPath(seedCatalogPath);
        var storageDirectory = Path.GetDirectoryName(StoragePath) ?? AppContext.BaseDirectory;
        FileStoreSnapshot.EnsureSafeDirectoryPath(storageDirectory);
        Directory.CreateDirectory(storageDirectory);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public string StoragePath { get; }

    public IReadOnlyList<ConfigurationLoadIssue> LoadIssues => _loadIssues.ToArray();

    public async Task<IReadOnlyList<CommunityProfileEntry>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        _loadIssues.Clear();
        try
        {
            FileStoreSnapshot.EnsureSafeFilePath(StoragePath);
            await EnsureSeededAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AddLoadIssue($"The community profile catalog path is unsafe or unavailable: {exception.Message}");
            return Array.Empty<CommunityProfileEntry>();
        }

        if (!File.Exists(StoragePath))
        {
            return Array.Empty<CommunityProfileEntry>();
        }

        List<CommunityProfileEntry?> entries;
        try
        {
            FileStoreSnapshot.EnsureSafeFilePath(StoragePath);
            await using var stream = File.OpenRead(StoragePath);
            entries = await JsonSerializer.DeserializeAsync<List<CommunityProfileEntry?>>(stream, _jsonOptions, cancellationToken)
                ?? new List<CommunityProfileEntry?>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AddLoadIssue($"The community profile catalog could not be loaded: {exception.Message}");
            return Array.Empty<CommunityProfileEntry>();
        }

        var compatibleEntries = new List<CommunityProfileEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                AddLoadIssue("The community profile catalog contains a null entry.");
                continue;
            }

            try
            {
                compatibleEntries.Add(ConfigurationSchemaPolicy.ValidateCommunityProfileEntry(
                    entry,
                    $"{StoragePath}#{entry.EntryId}"));
            }
            catch (Exception exception) when (
                exception is UnsupportedConfigurationSchemaException or
                InvalidProfileConfigurationException or
                ArgumentException)
            {
                _loadIssues.Add(new ConfigurationLoadIssue
                {
                    SourcePath = StoragePath,
                    Message = exception.Message
                });
            }
        }

        return compatibleEntries
            .OrderByDescending(entry => entry.Source == CommunityProfileSource.Official)
            .ThenByDescending(entry => entry.Downloads)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SaveProfilesAsync(IReadOnlyList<CommunityProfileEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var validEntries = new List<CommunityProfileEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            validEntries.Add(ConfigurationSchemaPolicy.ValidateCommunityProfileEntry(
                entry,
                $"{StoragePath}#{entry.EntryId}"));
        }

        FileStoreSnapshot.EnsureSafeFilePath(StoragePath);
        await AtomicFile.WriteAsync(
            StoragePath,
            async (destinationStream, token) =>
            {
                await JsonSerializer.SerializeAsync(destinationStream, validEntries, _jsonOptions, token);
            },
            cancellationToken);
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        FileStoreSnapshot.EnsureSafeFilePath(_seedCatalogPath);
        FileStoreSnapshot.EnsureSafeFilePath(StoragePath);
        if (File.Exists(StoragePath) || !File.Exists(_seedCatalogPath))
        {
            return;
        }

            await AtomicFile.WriteAsync(
            StoragePath,
            async (destinationStream, token) =>
            {
                FileStoreSnapshot.EnsureSafeFilePath(_seedCatalogPath);
                await using var sourceStream = File.OpenRead(_seedCatalogPath);
                await sourceStream.CopyToAsync(destinationStream, token);
            },
            cancellationToken);
    }

    private void AddLoadIssue(string message) =>
        _loadIssues.Add(new ConfigurationLoadIssue
        {
            SourcePath = StoragePath,
            Message = message
        });
}
