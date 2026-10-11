using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SuiXinTiao.Community.Api.Services;

namespace SuiXinTiao.Community.Api.Tests;

public sealed class CommunityCatalogStoreTests
{
    [Fact]
    public async Task ManifestAndSearchUseCatalogData()
    {
        using var fixture = new CatalogFixture();
        await fixture.WriteAsync("profiles.json", """
        [
          { "entryId": "browser-balanced", "name": "Browser Balanced", "author": "Official", "summary": "Browser profile", "source": "Official" },
          { "entryId": "game-performance", "name": "Game Performance", "author": "Community", "summary": "Game profile", "source": "Community" }
        ]
        """);
        await fixture.WriteAsync("device-strategies.json", "[{\"entryId\":\"sample-device\",\"name\":\"Sample Device\"}]");

        var manifest = await fixture.Store.GetManifestAsync(CancellationToken.None);
        var results = await fixture.Store.SearchProfilesAsync("browser", "Official", CancellationToken.None);

        Assert.Equal(2, manifest.ProfileCount);
        Assert.Equal(1, manifest.DeviceStrategyCount);
        var result = Assert.Single(results);
        Assert.Equal("browser-balanced", result.GetProperty("entryId").GetString());
    }

    [Fact]
    public async Task MissingCatalogIsAnEmptyCatalog()
    {
        using var fixture = new CatalogFixture();

        var manifest = await fixture.Store.GetManifestAsync(CancellationToken.None);
        var profiles = await fixture.Store.SearchProfilesAsync(null, null, CancellationToken.None);

        Assert.Equal(0, manifest.ProfileCount);
        Assert.Equal(0, manifest.DeviceStrategyCount);
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task InvalidCatalogRootIsReportedAsCatalogFailure()
    {
        using var fixture = new CatalogFixture();
        await fixture.WriteAsync("profiles.json", "{ \"entryId\": \"not-an-array\" }");

        var exception = await Assert.ThrowsAsync<CommunityCatalogException>(
            () => fixture.Store.SearchProfilesAsync(null, null, CancellationToken.None));

        Assert.Contains("profiles.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(fixture.DirectoryPath, "profiles.json"), exception.Path);
    }

    private sealed class CatalogFixture : IDisposable
    {
        public CatalogFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "SuiXinTiaoCommunityTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Community:DataDirectory"] = DirectoryPath
                })
                .Build();

            Store = new CommunityCatalogStore(new TestHostEnvironment(DirectoryPath), configuration);
        }

        public string DirectoryPath { get; }

        public CommunityCatalogStore Store { get; }

        public Task WriteAsync(string fileName, string content) =>
            File.WriteAllTextAsync(Path.Combine(DirectoryPath, fileName), content);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "SuiXinTiao.Community.Api.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
