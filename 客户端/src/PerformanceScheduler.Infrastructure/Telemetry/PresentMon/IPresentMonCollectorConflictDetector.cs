namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public interface IPresentMonCollectorConflictDetector
{
    IReadOnlyList<string> FindPotentialConflicts();
}
