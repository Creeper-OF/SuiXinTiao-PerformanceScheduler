using System.Text.Json;
using SuiXinTiao.Community.Api.Models;
using SuiXinTiao.Community.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<CommunityCatalogStore>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "随芯调社区服务",
    service = "SuiXinTiao Community API",
    apiVersion = "v1"
}));

app.MapGet("/api/v1/health", () => Results.Ok(new HealthResponse
{
    Status = "ok",
    Service = "community-api",
    ApiVersion = "v1",
    Utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/community/manifest", async (
    CommunityCatalogStore store,
    CancellationToken cancellationToken) =>
    await ExecuteCatalogRequestAsync(
        () => store.GetManifestAsync(cancellationToken)));

app.MapGet("/api/v1/community/profiles", async (
    string? q,
    string? source,
    CommunityCatalogStore store,
    CancellationToken cancellationToken) =>
    await ExecuteCatalogRequestAsync(
        () => store.SearchProfilesAsync(q, source, cancellationToken)));

app.MapGet("/api/v1/community/profiles/{entryId}", async (
    string entryId,
    CommunityCatalogStore store,
    CancellationToken cancellationToken) =>
{
    try
    {
        var entry = await store.FindProfileAsync(entryId, cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry.Value);
    }
    catch (CommunityCatalogException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Community catalog unavailable");
    }
});

app.MapGet("/api/v1/community/device-strategies", async (
    string? q,
    string? source,
    CommunityCatalogStore store,
    CancellationToken cancellationToken) =>
    await ExecuteCatalogRequestAsync(
        () => store.SearchDeviceStrategiesAsync(q, source, cancellationToken)));

app.MapGet("/api/v1/community/device-strategies/{entryId}", async (
    string entryId,
    CommunityCatalogStore store,
    CancellationToken cancellationToken) =>
{
    try
    {
        var entry = await store.FindDeviceStrategyAsync(entryId, cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry.Value);
    }
    catch (CommunityCatalogException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Community catalog unavailable");
    }
});

app.Run();

static async Task<IResult> ExecuteCatalogRequestAsync<T>(Func<Task<T>> operation)
{
    try
    {
        return Results.Ok(await operation());
    }
    catch (CommunityCatalogException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Community catalog unavailable");
    }
}

public partial class Program
{
}
