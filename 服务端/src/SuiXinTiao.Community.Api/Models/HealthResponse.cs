namespace SuiXinTiao.Community.Api.Models;

public sealed record HealthResponse
{
    public string Status { get; init; } = "ok";

    public string Service { get; init; } = "community-api";

    public string ApiVersion { get; init; } = "v1";

    public DateTimeOffset Utc { get; init; }
}
