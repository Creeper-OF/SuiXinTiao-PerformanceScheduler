namespace PerformanceScheduler.App.Services;

public sealed class StartupCommandLineArgs
{
    public const string SilentArgument = "--silent";
    public const string MinimizedArgument = "--minimized";

    public bool IsSilent { get; }
    public string? ScreenshotDirectory { get; }

    public StartupCommandLineArgs(bool isSilent, string? screenshotDirectory = null)
    {
        IsSilent = isSilent;
        ScreenshotDirectory = screenshotDirectory;
    }

    public static StartupCommandLineArgs Parse(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return new StartupCommandLineArgs(false);
        }

        bool isSilent = false;
        string? screenshotDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, SilentArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, MinimizedArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/minimized", StringComparison.OrdinalIgnoreCase))
            {
                isSilent = true;
            }
            else if ((string.Equals(arg, "--screenshot", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(arg, "-screenshot", StringComparison.OrdinalIgnoreCase)) &&
                     i + 1 < args.Length)
            {
                screenshotDir = args[++i];
            }
        }

        return new StartupCommandLineArgs(isSilent, screenshotDir);
    }
}
