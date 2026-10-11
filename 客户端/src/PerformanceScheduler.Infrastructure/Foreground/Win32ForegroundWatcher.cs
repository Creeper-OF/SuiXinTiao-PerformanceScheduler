using System.Runtime.InteropServices;
using System.Text;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Foreground;

public sealed class Win32ForegroundWatcher : IForegroundWatcher
{
    public Task<ForegroundWindowSnapshot?> GetForegroundWindowAsync(CancellationToken cancellationToken = default)
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return Task.FromResult<ForegroundWindowSnapshot?>(null);
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        var titleLength = GetWindowTextLength(handle);
        var builder = new StringBuilder(titleLength + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);

        return Task.FromResult<ForegroundWindowSnapshot?>(new ForegroundWindowSnapshot(
            handle,
            unchecked((int)processId),
            builder.ToString()));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
