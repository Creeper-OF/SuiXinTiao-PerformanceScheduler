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

            return Task.FromResult<FocusedAppContext?>(new FocusedAppContext
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                ExecutablePath = executablePath,
                WindowTitle = string.IsNullOrWhiteSpace(snapshot.WindowTitle)
                    ? process.MainWindowTitle
                    : snapshot.WindowTitle,
                WindowHandle = snapshot.WindowHandle,
                Classification = ProcessClassificationHelper.Classify(process.ProcessName, executablePath, snapshot.WindowTitle)
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
            return process.MainModule?.FileName;
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
