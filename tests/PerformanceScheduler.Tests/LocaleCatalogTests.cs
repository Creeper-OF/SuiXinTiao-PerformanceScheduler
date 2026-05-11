using System.Text.Json;

namespace PerformanceScheduler.Tests;

public sealed class LocaleCatalogTests
{
    [Fact]
    public void Locales_HaveMatchingKeys()
    {
        var english = LoadCatalog("en-US");
        var chinese = LoadCatalog("zh-CN");

        Assert.Empty(english.Keys.Except(chinese.Keys).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(chinese.Keys.Except(english.Keys).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChineseLocale_DoesNotContainCommonMojibakeMarkers()
    {
        var chinese = LoadCatalog("zh-CN");
        var suspiciousMarkers = new[] { "\uFFFD", "鎬", "璋", "鍓", "鐢", "绠", "€" };

        foreach (var (key, value) in chinese)
        {
            Assert.DoesNotContain("????", value, StringComparison.Ordinal);
            foreach (var marker in suspiciousMarkers)
            {
                Assert.DoesNotContain(marker, value, StringComparison.Ordinal);
            }
        }
    }

    private static Dictionary<string, string> LoadCatalog(string languageCode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "locales", $"{languageCode}.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }
}
