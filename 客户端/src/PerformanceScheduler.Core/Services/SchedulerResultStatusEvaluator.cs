using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public static class SchedulerResultStatusEvaluator
{
    public static int CountUnsupportedActions(IEnumerable<SchedulerActionResult> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        return actions.Count(action => action.Status == SchedulerActionStatus.Unsupported);
    }

    public static bool HasUnsupportedActions(IEnumerable<SchedulerActionResult> actions) =>
        CountUnsupportedActions(actions) > 0;
}
