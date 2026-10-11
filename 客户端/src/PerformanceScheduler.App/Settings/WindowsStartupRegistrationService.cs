using Microsoft.Win32;

namespace PerformanceScheduler.App.Settings;

public sealed class WindowsStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PerformanceScheduler";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled, bool launchSilently = true)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return;
            }

            var commandValue = launchSilently
                ? FormatCommandLine(processPath, "--silent")
                : FormatCommandLine(processPath);

            key.SetValue(ValueName, commandValue);
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static string FormatCommandLine(string processPath, string? argument = null)
    {
        return string.IsNullOrWhiteSpace(argument)
            ? $"\"{processPath}\""
            : $"\"{processPath}\" {argument.Trim()}";
    }
}
