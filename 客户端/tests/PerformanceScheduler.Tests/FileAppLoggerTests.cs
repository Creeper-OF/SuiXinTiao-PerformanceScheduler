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

    [Fact]
    public void Info_DoesNotThrowWhenLogDirectoryIsAnExistingFile()
    {
        Directory.CreateDirectory(_tempDirectory);
        var invalidLogDirectory = Path.Combine(_tempDirectory, "not-a-directory");
        File.WriteAllText(invalidLogDirectory, "reserved");
        var logger = new FileAppLogger(invalidLogDirectory);

        var exception = Record.Exception(() => logger.Info("In-memory fallback."));

        Assert.Null(exception);
        Assert.Contains(logger.GetRecentEntries(), entry => entry.Message == "In-memory fallback.");
    }

    [Fact]
    public void Info_DoesNotPropagateSubscriberFailures()
    {
        var logger = new FileAppLogger(_tempDirectory);
        logger.EntryWritten += (_, _) => throw new InvalidOperationException("closed dispatcher");

        var exception = Record.Exception(() => logger.Info("Subscriber failure fallback."));

        Assert.Null(exception);
        Assert.Contains(logger.GetRecentEntries(), entry => entry.Message == "Subscriber failure fallback.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
