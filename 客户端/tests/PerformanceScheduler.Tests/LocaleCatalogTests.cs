using System.Text.Json;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

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

    [Fact]
    public void Locales_DescribeEverySchedulerActionReasonCode()
    {
        var english = LoadCatalog("en-US");
        var chinese = LoadCatalog("zh-CN");

        foreach (var reasonCode in Enum.GetValues<SchedulerActionReasonCode>()
                     .Where(code => code != SchedulerActionReasonCode.None))
        {
            var reasonKey = $"SchedulerActionReason.{reasonCode}";
            var suggestionKey = $"SchedulerActionSuggestion.{reasonCode}";
            Assert.Contains(reasonKey, english.Keys);
            Assert.Contains(reasonKey, chinese.Keys);
            Assert.Contains(suggestionKey, english.Keys);
            Assert.Contains(suggestionKey, chinese.Keys);
        }
    }

    [Fact]
    public void Locales_DescribeEveryUserEventAndLogLabel()
    {
        var english = LoadCatalog("en-US");
        var chinese = LoadCatalog("zh-CN");

        foreach (var eventCode in Enum.GetValues<UserEventCode>()
                     .Where(code => code != UserEventCode.None))
        {
            AssertLocalized($"UserEvent.{eventCode}", english, chinese);
        }

        foreach (var level in Enum.GetValues<AppLogLevel>())
        {
            AssertLocalized($"Enum.AppLogLevel.{level}", english, chinese);
        }

        foreach (var audience in Enum.GetValues<LogAudience>())
        {
            AssertLocalized($"Enum.LogAudience.{audience}", english, chinese);
        }
    }

    [Fact]
    public void Locales_DescribeEveryTelemetryStatusMetricAndReason()
    {
        var english = LoadCatalog("en-US");
        var chinese = LoadCatalog("zh-CN");

        foreach (var status in Enum.GetValues<TelemetryCollectionStatus>())
        {
            AssertLocalized($"Enum.TelemetryCollectionStatus.{status}", english, chinese);
        }

        foreach (var metric in Enum.GetValues<TelemetryMetricKind>())
        {
            AssertLocalized($"Enum.TelemetryMetricKind.{metric}", english, chinese);
        }

        foreach (var field in typeof(TelemetryReasonCodes).GetFields(
                     System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Static))
        {
            var key = Assert.IsType<string>(field.GetRawConstantValue());
            AssertLocalized(key, english, chinese);
        }
    }

    private static void AssertLocalized(
        string key,
        IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> chinese)
    {
        Assert.Contains(key, english.Keys);
        Assert.Contains(key, chinese.Keys);
    }

    private static Dictionary<string, string> LoadCatalog(string languageCode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "locales", $"{languageCode}.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }
}
