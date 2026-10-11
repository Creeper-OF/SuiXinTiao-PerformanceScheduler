using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Community;

public sealed class HttpCommunityApiClient : ICommunityApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public HttpCommunityApiClient(Uri? baseUri = null, HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            if (baseUri is not null)
            {
                _httpClient.BaseAddress = EnsureTrailingSlash(baseUri);
            }
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("PerformanceScheduler-Client", "1.0"));
            _disposeClient = true;
        }
        else
        {
            _httpClient = httpClient;
            if (baseUri is not null && _httpClient.BaseAddress is null)
            {
                _httpClient.BaseAddress = EnsureTrailingSlash(baseUri);
            }
            _disposeClient = false;
        }
    }

    public HttpCommunityApiClient(string? baseUri, HttpClient? httpClient = null)
        : this(string.IsNullOrWhiteSpace(baseUri) ? null : new Uri(baseUri), httpClient)
    {
    }

    public Uri? BaseAddress => _httpClient.BaseAddress;

    public async Task<CommunityApiHealth?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/v1/health";
        return await SendGetAsync<CommunityApiHealth>(endpoint, cancellationToken);
    }

    public async Task<CommunityCatalogManifest?> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/v1/community/manifest";
        return await SendGetAsync<CommunityCatalogManifest>(endpoint, cancellationToken);
    }

    public async Task<IReadOnlyList<CommunityProfileEntry>> FetchProfilesAsync(
        string? query = null,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildQueryEndpoint("api/v1/community/profiles", query, source);
        var entries = await SendGetAsync<List<CommunityProfileEntry>>(endpoint, cancellationToken);
        return entries ?? (IReadOnlyList<CommunityProfileEntry>)Array.Empty<CommunityProfileEntry>();
    }

    public async Task<CommunityProfileEntry?> FetchProfileAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var endpoint = $"api/v1/community/profiles/{Uri.EscapeDataString(entryId)}";
        return await SendGetAsync<CommunityProfileEntry>(endpoint, cancellationToken, allowNotFound: true);
    }

    public async Task<IReadOnlyList<CommunityDeviceStrategyEntry>> FetchDeviceStrategiesAsync(
        string? query = null,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildQueryEndpoint("api/v1/community/device-strategies", query, source);
        var entries = await SendGetAsync<List<CommunityDeviceStrategyEntry>>(endpoint, cancellationToken);
        return entries ?? (IReadOnlyList<CommunityDeviceStrategyEntry>)Array.Empty<CommunityDeviceStrategyEntry>();
    }

    public async Task<CommunityDeviceStrategyEntry?> FetchDeviceStrategyAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var endpoint = $"api/v1/community/device-strategies/{Uri.EscapeDataString(entryId)}";
        return await SendGetAsync<CommunityDeviceStrategyEntry>(endpoint, cancellationToken, allowNotFound: true);
    }

    private async Task<T?> SendGetAsync<T>(string endpoint, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new CommunityApiException(
                    $"Community API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {content}",
                    endpoint,
                    (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CommunityApiException("Community API request timed out.", endpoint, null, exception);
        }
        catch (HttpRequestException exception)
        {
            var statusCode = exception.StatusCode.HasValue ? (int)exception.StatusCode.Value : (int?)null;
            throw new CommunityApiException(
                $"Failed to connect to community API endpoint '{endpoint}': {exception.Message}",
                endpoint,
                statusCode,
                exception);
        }
        catch (JsonException exception)
        {
            throw new CommunityApiException(
                $"Failed to deserialize response from community API endpoint '{endpoint}': {exception.Message}",
                endpoint,
                null,
                exception);
        }
    }

    private static string BuildQueryEndpoint(string basePath, string? query, string? source)
    {
        var queryParameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            queryParameters.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            queryParameters.Add($"source={Uri.EscapeDataString(source.Trim())}");
        }

        return queryParameters.Count > 0
            ? $"{basePath}?{string.Join("&", queryParameters)}"
            : basePath;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var uriString = uri.ToString();
        return uriString.EndsWith('/') ? uri : new Uri(uriString + "/");
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
