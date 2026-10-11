using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IProcessInspector
{
    Task<FocusedAppContext?> InspectAsync(
        ForegroundWindowSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
