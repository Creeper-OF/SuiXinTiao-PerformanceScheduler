using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ISchedulerActionExecutor
{
    Task<SchedulerActionExecutionResult> ExecuteAsync(
        SchedulerActionExecutionRequest request,
        CancellationToken cancellationToken = default);
}
