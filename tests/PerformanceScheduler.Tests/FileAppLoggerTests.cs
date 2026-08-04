using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Diagnostics;

namespace PerformanceScheduler.Tests;

public sealed class FileAppLoggerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PublishUserEvent_PreservesStructuredMetadataAndWritesTags()
    {
        var logger = new FileAppLogger(_tempDirectory);
        var transactionId = Guid.NewGuid();

        logger.PublishUserEvent(
            UserEventCode.FailedRunRollbackIncomplete,
            AppLogLevel.Warning,
            $"Rollback incomplete for {transactionId}.",
            transactionId);

        var entry = Assert.Single(logger.GetRecentEntries());
        Assert.Equal(LogAudience.User, entry.Audience);
        Assert.Equal(UserEventCode.FailedRunRollbackIncomplete, entry.EventCode);
        Assert.Equal(transactionId, Assert.Single(entry.Arguments!));

        var logText = File.ReadAllText(Path.Combine(_tempDirectory, "performance-scheduler.log"));
        Assert.Contains("[Warning] [User] [FailedRunRollbackIncomplete]", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Info_UsesDeveloperAudienceByDefault()
    {
        var logger = new FileAppLogger(_tempDirectory);

        logger.Info("Runtime initialized.");

        var entry = Assert.Single(logger.GetRecentEntries());
        Assert.Equal(LogAudience.Developer, entry.Audience);
        Assert.Equal(UserEventCode.None, entry.EventCode);
        Assert.Null(entry.Arguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
