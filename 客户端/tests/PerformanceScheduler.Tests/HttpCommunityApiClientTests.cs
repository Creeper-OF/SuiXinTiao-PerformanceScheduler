using System.Net;
using System.Text;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Community;

namespace PerformanceScheduler.Tests;

public sealed class HttpCommunityApiClientTests
{
    [Fact]
    public async Task GetHealthAsync_WhenServerReturnsOk_ReturnsHealthModel()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal("/api/v1/health", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"ok","service":"community-api","apiVersion":"v1","utc":"2026-10-08T00:00:00Z"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var health = await apiClient.GetHealthAsync();

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal("community-api", health.Service);
        Assert.Equal("v1", health.ApiVersion);
    }

    [Fact]
    public async Task GetManifestAsync_WhenServerReturnsManifest_ReturnsManifestModel()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal("/api/v1/community/manifest", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"apiVersion":"v1","profileCount":5,"deviceStrategyCount":2,"generatedAtUtc":"2026-10-08T12:00:00Z"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var manifest = await apiClient.GetManifestAsync();

        Assert.NotNull(manifest);
        Assert.Equal("v1", manifest.ApiVersion);
        Assert.Equal(5, manifest.ProfileCount);
        Assert.Equal(2, manifest.DeviceStrategyCount);
    }

    [Fact]
    public async Task FetchProfilesAsync_IncludesQueryAndSourceParameters()
    {
        HttpRequestMessage? recordedRequest = null;
        var handler = new MockHttpMessageHandler(request =>
        {
            recordedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"schemaVersion":1,"entryId":"test-1","name":"Test Profile","profile":{"schemaVersion":1,"id":"test-1"}}]""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var results = await apiClient.FetchProfilesAsync(query: "game mode", source: "Official");

        Assert.NotNull(recordedRequest);
        Assert.Contains("q=game%20mode", recordedRequest.RequestUri!.Query);
        Assert.Contains("source=Official", recordedRequest.RequestUri!.Query);
        var entry = Assert.Single(results);
        Assert.Equal("test-1", entry.EntryId);
        Assert.Equal("Test Profile", entry.Name);
    }

    [Fact]
    public async Task FetchProfileAsync_WhenNotFound_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal("/api/v1/community/profiles/missing-id", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var result = await apiClient.FetchProfileAsync("missing-id");

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchProfileAsync_WhenFound_ReturnsProfileEntry()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal("/api/v1/community/profiles/found-id", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"schemaVersion":1,"entryId":"found-id","name":"Found Profile","profile":{"schemaVersion":1,"id":"found-id"}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var result = await apiClient.FetchProfileAsync("found-id");

        Assert.NotNull(result);
        Assert.Equal("found-id", result.EntryId);
        Assert.Equal("Found Profile", result.Name);
    }

    [Fact]
    public async Task FetchDeviceStrategiesAsync_ReturnsDeviceStrategiesList()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal("/api/v1/community/device-strategies", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"schemaVersion":1,"entryId":"strat-1","name":"Test Strategy","package":{"schemaVersion":1,"packageId":"pkg-1","profiles":[]}}]""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var results = await apiClient.FetchDeviceStrategiesAsync();

        var entry = Assert.Single(results);
        Assert.Equal("strat-1", entry.EntryId);
        Assert.Equal("Test Strategy", entry.Name);
    }

    [Fact]
    public async Task FetchDeviceStrategyAsync_WhenNotFound_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var result = await apiClient.FetchDeviceStrategyAsync("not-there");

        Assert.Null(result);
    }

    [Fact]
    public async Task SendGetAsync_WhenServerReturns503_ThrowsCommunityApiException()
    {
        var handler = new MockHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Catalog unavailable", Encoding.UTF8, "text/plain")
            });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var apiClient = new HttpCommunityApiClient(httpClient: client);

        var exception = await Assert.ThrowsAsync<CommunityApiException>(() => apiClient.GetHealthAsync());
        Assert.Equal(503, exception.StatusCode);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }
}
