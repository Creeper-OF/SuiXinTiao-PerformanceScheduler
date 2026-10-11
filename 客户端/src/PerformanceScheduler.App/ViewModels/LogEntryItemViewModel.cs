using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class LogEntryItemViewModel
{
    public required LogEntry Entry { get; init; }

    public DateTimeOffset Timestamp => Entry.Timestamp;

    public required string Level { get; init; }

    public required string Audience { get; init; }

    public required string Message { get; init; }

    public string TechnicalMessage => Entry.Message;
}
