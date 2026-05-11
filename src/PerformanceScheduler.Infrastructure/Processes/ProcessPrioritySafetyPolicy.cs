using System.Diagnostics;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Processes;

internal static class ProcessPrioritySafetyPolicy
{
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg",
        "conhost",
        "csrss",
        "dwm",
        "fontdrvhost",
        "lsass",
        "memory compression",
        "registry",
        "runtimebroker",
        "securityhealthservice",
        "services",
        "sihost",
        "smss",
        "spoolsv",
        "system",
        "system idle process",
        "taskhostw",
        "wininit",
        "winlogon",
        "wudfhost"
    };

    public static PrioritySafetyDecision Evaluate(Process process, PriorityLevel targetPriority)
    {
        if (targetPriority == PriorityLevel.RealTime)
        {
            return PrioritySafetyDecision.Block("RealTime priority is blocked by the safety policy.");
        }

        var processId = SafeProcessId(process);
        if (processId <= 0)
        {
            return PrioritySafetyDecision.Block("System pseudo-processes are protected.");
        }

        if (processId == Environment.ProcessId)
        {
            return PrioritySafetyDecision.Block("The scheduler process is protected.");
        }

        var processName = SafeProcessName(process);
        if (ProtectedProcessNames.Contains(processName))
        {
            return PrioritySafetyDecision.Block($"Protected Windows process: {processName}.");
        }

        return PrioritySafetyDecision.Allow();
    }

    public static PrioritySafetyDecision Evaluate(FocusedAppContext app, PriorityLevel targetPriority)
    {
        if (targetPriority == PriorityLevel.RealTime)
        {
            return PrioritySafetyDecision.Block("RealTime priority is blocked by the safety policy.");
        }

        if (app.ProcessId <= 0)
        {
            return PrioritySafetyDecision.Block("System pseudo-processes are protected.");
        }

        if (app.ProcessId == Environment.ProcessId)
        {
            return PrioritySafetyDecision.Block("The scheduler process is protected.");
        }

        if (ProtectedProcessNames.Contains(app.ProcessName))
        {
            return PrioritySafetyDecision.Block($"Protected Windows process: {app.ProcessName}.");
        }

        return PrioritySafetyDecision.Allow();
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal readonly record struct PrioritySafetyDecision(bool IsAllowed, string Reason)
{
    public static PrioritySafetyDecision Allow() => new(true, string.Empty);

    public static PrioritySafetyDecision Block(string reason) => new(false, reason);
}
