using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IConfigurationLoadIssueSource
{
    IReadOnlyList<ConfigurationLoadIssue> LoadIssues { get; }
}
