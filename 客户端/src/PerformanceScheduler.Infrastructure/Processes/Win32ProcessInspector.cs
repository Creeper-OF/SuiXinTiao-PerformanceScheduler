using System.ComponentModel;
using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32ProcessInspector : IProcessInspector
{
    public Task<FocusedAppContext?> InspectAsync(
        ForegroundWindowSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            var executablePath = TryReadExecutablePath(process);
            var windowTitle = string.IsNullOrWhiteSpace(snapshot.WindowTitle)
                ? process.MainWindowTitle
                : snapshot.WindowTitle;

            return Task.FromResult<FocusedAppContext?>(new FocusedAppContext
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                ProcessStartedAt = TryReadProcessStartTime(process),
                ExecutablePath = executablePath,
                WindowTitle = windowTitle,
                WindowHandle = snapshot.WindowHandle,
                Classification = ProcessClassificationHelper.Classify(process.ProcessName, executablePath, windowTitle)
            });
        }
        catch
        {
            return Task.FromResult<FocusedAppContext?>(null);
        }
    }

    private static string? TryReadExecutablePath(Process process)
    {
        try
        {
            return WindowsProcessStateReader.TryReadImagePath(process);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadProcessStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

}
