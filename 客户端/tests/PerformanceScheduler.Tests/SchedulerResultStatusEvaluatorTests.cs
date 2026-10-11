using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class SchedulerResultStatusEvaluatorTests
{
    [Fact]
    public void CountUnsupportedActions_ReturnsOnlyUnsupportedActions()
    {
        var actions = new[]
        {
            new SchedulerActionResult { Status = SchedulerActionStatus.Applied },
            new SchedulerActionResult { Status = SchedulerActionStatus.Unsupported },
            new SchedulerActionResult { Status = SchedulerActionStatus.Failed },
            new SchedulerActionResult { Status = SchedulerActionStatus.Unsupported }
        };

        Assert.Equal(2, SchedulerResultStatusEvaluator.CountUnsupportedActions(actions));
        Assert.True(SchedulerResultStatusEvaluator.HasUnsupportedActions(actions));
    }

    [Fact]
    public void HasUnsupportedActions_ReturnsFalseForEmptyResults()
    {
        Assert.False(SchedulerResultStatusEvaluator.HasUnsupportedActions(Array.Empty<SchedulerActionResult>()));
    }

    [Fact]
    public void CountUnsupportedActions_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SchedulerResultStatusEvaluator.CountUnsupportedActions(null!));
    }
}
