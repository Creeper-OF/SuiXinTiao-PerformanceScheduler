namespace SuiXinTiao.Community.Api.Models;

public sealed record CommunityCatalogManifest
{
    public string ApiVersion { get; init; } = "v1";

    public int ProfileCount { get; init; }

    public int DeviceStrategyCount { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }
}
