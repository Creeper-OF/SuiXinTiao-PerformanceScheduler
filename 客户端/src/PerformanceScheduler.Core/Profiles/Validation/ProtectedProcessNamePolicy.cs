namespace PerformanceScheduler.Core.Profiles.Validation;

public static class ProtectedProcessNamePolicy
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

    public static bool IsProtected(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalizedName = Path.GetFileNameWithoutExtension(processName.Trim());
        return ProtectedProcessNames.Contains(normalizedName);
    }
}
