using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonCommunityProfileCatalog : ICommunityProfileCatalog
{
    private readonly string _seedCatalogPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonCommunityProfileCatalog(string storagePath, string seedCatalogPath)
    {
        StoragePath = storagePath;
        _seedCatalogPath = seedCatalogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath) ?? AppContext.BaseDirectory);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public string StoragePath { get; }

    public async Task<IReadOnlyList<CommunityProfileEntry>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        if (!File.Exists(StoragePath))
        {
            return Array.Empty<CommunityProfileEntry>();
        }

        List<CommunityProfileEntry> entries;
        try
        {
            await using var stream = File.OpenRead(StoragePath);
            entries = await JsonSerializer.DeserializeAsync<List<CommunityProfileEntry>>(stream, _jsonOptions, cancellationToken)
                ?? new List<CommunityProfileEntry>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<CommunityProfileEntry>();
        }

        return entries
            .OrderByDescending(entry => entry.Source == CommunityProfileSource.Official)
            .ThenByDescending(entry => entry.Downloads)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(StoragePath) || !File.Exists(_seedCatalogPath))
        {
            return;
        }

        await using var sourceStream = File.OpenRead(_seedCatalogPath);
        await using var destinationStream = File.Create(StoragePath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }
}
