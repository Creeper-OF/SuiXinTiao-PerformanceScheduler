using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class JsonCommunityDeviceStrategyCatalog : ICommunityDeviceStrategyCatalog, IConfigurationLoadIssueSource
{
    private readonly string _seedCatalogPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();

    public JsonCommunityDeviceStrategyCatalog(string storagePath, string seedCatalogPath)
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

    public IReadOnlyList<ConfigurationLoadIssue> LoadIssues => _loadIssues.ToArray();

    public async Task<IReadOnlyList<CommunityDeviceStrategyEntry>> LoadStrategiesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);
        if (!File.Exists(StoragePath))
        {
            return Array.Empty<CommunityDeviceStrategyEntry>();
        }

        List<CommunityDeviceStrategyEntry> entries;
        try
        {
            await using var stream = File.OpenRead(StoragePath);
            entries = await JsonSerializer.DeserializeAsync<List<CommunityDeviceStrategyEntry>>(stream, _jsonOptions, cancellationToken)
                      ?? new List<CommunityDeviceStrategyEntry>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<CommunityDeviceStrategyEntry>();
        }

        _loadIssues.Clear();
        var compatibleEntries = new List<CommunityDeviceStrategyEntry>(entries.Count);
        foreach (var entry in entries)
        {
            try
            {
                compatibleEntries.Add(ConfigurationSchemaPolicy.ValidateCommunityDeviceStrategyEntry(
                    entry,
                    $"{StoragePath}#{entry.EntryId}"));
            }
            catch (UnsupportedConfigurationSchemaException exception)
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

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(StoragePath) || !File.Exists(_seedCatalogPath))
        {
            return;
        }

        await AtomicFile.WriteAsync(
            StoragePath,
            async (destinationStream, token) =>
            {
                await using var sourceStream = File.OpenRead(_seedCatalogPath);
                await sourceStream.CopyToAsync(destinationStream, token);
            },
            cancellationToken);
    }
}
