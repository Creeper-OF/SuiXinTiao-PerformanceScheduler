namespace PerformanceScheduler.Core.Models;

public sealed record CommunitySyncResult
{
    public bool IsSuccess { get; init; }

    public bool IsRemoteAvailable { get; init; }

    public int ProfilesCount { get; init; }

    public int DeviceStrategiesCount { get; init; }

    public string? Message { get; init; }

    public DateTimeOffset SyncedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static CommunitySyncResult Success(int profilesCount, int deviceStrategiesCount, string? message = null) =>
        new()
        {
            IsSuccess = true,
            IsRemoteAvailable = true,
            ProfilesCount = profilesCount,
            DeviceStrategiesCount = deviceStrategiesCount,
            Message = message ?? "Community catalog synchronized successfully."
        };

    public static CommunitySyncResult FailedOffline(string message, Exception? exception = null) =>
        new()
        {
            IsSuccess = false,
            IsRemoteAvailable = false,
            ProfilesCount = 0,
            DeviceStrategiesCount = 0,
            Message = exception is null ? message : $"{message} ({exception.Message})"
        };

    public static CommunitySyncResult Skipped(string message) =>
        new()
        {
            IsSuccess = false,
            IsRemoteAvailable = false,
            ProfilesCount = 0,
            DeviceStrategiesCount = 0,
            Message = message
        };
}
