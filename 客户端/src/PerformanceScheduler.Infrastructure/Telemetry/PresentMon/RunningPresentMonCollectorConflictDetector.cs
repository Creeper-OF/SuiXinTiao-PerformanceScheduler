using System.Diagnostics;

namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed class RunningPresentMonCollectorConflictDetector : IPresentMonCollectorConflictDetector
{
    private static readonly string[] KnownCollectorNames =
    [
        "CapFrameX",
        "OCAT",
        "FrameView",
        "IntelPresentMon"
    ];

    private readonly Func<IReadOnlyList<string>> _getRunningProcessNames;

    public RunningPresentMonCollectorConflictDetector(
        Func<IReadOnlyList<string>>? getRunningProcessNames = null)
    {
        _getRunningProcessNames = getRunningProcessNames ?? ReadRunningProcessNames;
    }

    public IReadOnlyList<string> FindPotentialConflicts() =>
        _getRunningProcessNames()
            .Where(IsKnownCollector)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsKnownCollector(string processName) =>
        KnownCollectorNames.Contains(processName, StringComparer.OrdinalIgnoreCase) ||
        processName.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadRunningProcessNames()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch
                {
                    // Processes can exit or become inaccessible during enumeration.
                }
            }
        }

        return names;
    }
}
