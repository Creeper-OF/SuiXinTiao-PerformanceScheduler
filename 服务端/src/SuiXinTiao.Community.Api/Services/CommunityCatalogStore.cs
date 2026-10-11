using System.Text.Json;
using SuiXinTiao.Community.Api.Models;

namespace SuiXinTiao.Community.Api.Services;

public sealed class CommunityCatalogStore
{
    private const string ProfilesFileName = "profiles.json";
    private const string DeviceStrategiesFileName = "device-strategies.json";

    private readonly string _dataDirectory;

    public CommunityCatalogStore(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredDirectory = configuration["Community:DataDirectory"];
        _dataDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.GetFullPath(configuredDirectory, environment.ContentRootPath);
    }

    public async Task<CommunityCatalogManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        var profiles = await ReadCatalogAsync(ProfilesFileName, cancellationToken);
        var deviceStrategies = await ReadCatalogAsync(DeviceStrategiesFileName, cancellationToken);
        return new CommunityCatalogManifest
        {
            ProfileCount = profiles.Count,
            DeviceStrategyCount = deviceStrategies.Count,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<IReadOnlyList<JsonElement>> SearchProfilesAsync(
        string? query,
        string? source,
        CancellationToken cancellationToken)
    {
        var entries = await ReadCatalogAsync(ProfilesFileName, cancellationToken);
        return entries.Where(entry => Matches(entry, query, source)).ToArray();
    }

    public async Task<IReadOnlyList<JsonElement>> SearchDeviceStrategiesAsync(
        string? query,
        string? source,
        CancellationToken cancellationToken)
    {
        var entries = await ReadCatalogAsync(DeviceStrategiesFileName, cancellationToken);
        return entries.Where(entry => Matches(entry, query, source)).ToArray();
    }

    public async Task<JsonElement?> FindProfileAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        var entries = await ReadCatalogAsync(ProfilesFileName, cancellationToken);
        return FindEntry(entries, entryId);
    }

    public async Task<JsonElement?> FindDeviceStrategyAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        var entries = await ReadCatalogAsync(DeviceStrategiesFileName, cancellationToken);
        return FindEntry(entries, entryId);
    }

    private async Task<IReadOnlyList<JsonElement>> ReadCatalogAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<JsonElement>();
            }

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The catalog root must be a JSON array.");
            }

            return document.RootElement
                .EnumerateArray()
                .Where(static entry => entry.ValueKind == JsonValueKind.Object)
                .Select(static entry => entry.Clone())
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CommunityCatalogException(
                $"Community catalog '{fileName}' could not be loaded.",
                path,
                exception);
        }
    }

    private static JsonElement? FindEntry(
        IEnumerable<JsonElement> entries,
        string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return null;
        }

        foreach (var entry in entries)
        {
            if (TryGetString(entry, "entryId") is { } candidate &&
                string.Equals(candidate, entryId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool Matches(JsonElement entry, string? query, string? source)
    {
        if (!string.IsNullOrWhiteSpace(source) &&
            !string.Equals(TryGetString(entry, "source"), source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return new[] { "entryId", "name", "author", "summary" }
            .Select(propertyName => TryGetString(entry, propertyName))
            .OfType<string>()
            .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetString(JsonElement entry, string propertyName) =>
        entry.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
